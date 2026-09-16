using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TianWen.DAL;

namespace QHYCCD.SDK;

public static partial class QHYCamera
{
    private static readonly Lock _sharedLock = new();
    private static readonly Dictionary<string, SharedHandleState> _sharedHandles = [];
    private static bool _resourceInitialized;

    private class SharedHandleState
    {
        public IntPtr Handle;
        public int RefCount;
        public bool Initialized;
    }

    /// <summary>
    /// Ensures <see cref="InitQHYCCDResource"/> has been called exactly once per process.
    /// Must be called before <see cref="ScanQHYCCD"/> or <see cref="OpenQHYCCD"/>.
    /// </summary>
    public static bool EnsureResourceInitialized()
    {
        lock (_sharedLock)
        {
            if (_resourceInitialized)
            {
                return true;
            }

            if (InitQHYCCDResource() is QHYCCD_SUCCESS)
            {
                _resourceInitialized = true;
                return true;
            }

            return false;
        }
    }

    public struct QHYCCD_CAMERA_INFO : ICMOSNativeInterface
    {
        private IntPtr _handle;
        private readonly string _id;
        private readonly string _model;
        private int _maxWidth;
        private int _maxHeight;
        private int _bitDepth;
        private int _adcBitDepth;
        private double _pixelSizeX;
        private double _pixelSizeY;
        private double _chipWidth;
        private double _chipHeight;
        private bool _isColor;
        private BAYER_ID _bayerId;
        private bool _hasCooler;
        private bool _hasST4Port;
        private bool _hasMechanicalShutter;
        private bool _isTriggerCamera;
        private bool _isUSB3;

        // Per-exposure state for QHY "read directly" cameras (ExpQHYCCDSingleFrame
        // returning QHYCCD_READ_DIRECTLY). Captured in StartExposureCore and consumed
        // by GetExposureStatus to apply QHY's October 2024 readout-timing rule.
        private bool _readDirectly;
        private long _exposureStartTicks;
        private double _exposureMicros;

        internal QHYCCD_CAMERA_INFO(string id) : this()
        {
            _id = id;
            _model = GetModelFromId(id);
        }

        internal IntPtr Handle => _handle;

        public int ID => _id?.GetHashCode() ?? 0;

        public string Name => _model;

        public string CustomId => _id;

        public string SerialNumber => _id;

        public bool IsUSB3Device => _isUSB3;

        string? INativeDeviceInfo.SensorModel =>
            TianWen.DAL.SensorModelNames.TryGetSensorModel(Name, out var model) ? model : null;

        /// <summary>
        /// Opens the camera. If another <see cref="QHYCCD_CAMERA_INFO"/> for the same camera ID
        /// is already open (e.g. a filter wheel driver sharing the camera handle), the native handle
        /// is shared via reference counting. <see cref="CloseQHYCCD"/> is only called when the last
        /// reference is released.
        /// </summary>
        public bool Open()
        {
            lock (_sharedLock)
            {
                if (_sharedHandles.TryGetValue(_id, out var state))
                {
                    // Share existing handle
                    _handle = state.Handle;
                    state.RefCount++;
                    QueryCapabilities();
                    return true;
                }
            }

            // First open — actually call into the native SDK
            var idBytes = Encoding.ASCII.GetBytes(_id + '\0');
            unsafe
            {
                fixed (byte* pId = idBytes)
                {
                    _handle = OpenQHYCCD((IntPtr)pId);
                }
            }

            if (_handle == IntPtr.Zero)
            {
                return false;
            }

            lock (_sharedLock)
            {
                _sharedHandles[_id] = new SharedHandleState { Handle = _handle, RefCount = 1 };
            }

            QueryCapabilities();
            return true;
        }

        /// <summary>
        /// Initializes the camera for single-frame mode. Calls <see cref="SetQHYCCDStreamMode"/>
        /// and <see cref="InitQHYCCD"/>. Safe to call multiple times — only the first call per
        /// shared handle takes effect.
        /// </summary>
        public bool Init()
        {
            bool alreadyInitialized;
            lock (_sharedLock)
            {
                alreadyInitialized = _sharedHandles.TryGetValue(_id, out var state) && state.Initialized;
            }

            if (alreadyInitialized)
            {
                // A handle-sharing struct (e.g. the CFW driver) skips re-init but still needs its own
                // copy's BitDepth refined -- the native handle is live, so the control is queryable.
                RefineAdcBitDepth();
                return true;
            }

            SetQHYCCDStreamMode(_handle, 0); // single frame mode
            var result = InitQHYCCD(_handle) is QHYCCD_SUCCESS;
            if (result)
            {
                lock (_sharedLock)
                {
                    if (_sharedHandles.TryGetValue(_id, out var state))
                    {
                        state.Initialized = true;
                    }
                }

                // OutputDataActualBits is only queryable once InitQHYCCD has run.
                RefineAdcBitDepth();
            }

            return result;
        }

        /// <summary>
        /// Refines the reported <see cref="BitDepth"/> from the container/transfer width that
        /// <see cref="GetQHYCCDChipInfo"/> reports (always 8 or 16) to the sensor's true ADC resolution
        /// when the camera exposes it via <see cref="CONTROL_ID.OutputDataActualBits"/> -- e.g. 14 for
        /// the QHY294. Requires <see cref="InitQHYCCD"/> to have run first (control params are only valid
        /// post-init), so it is called from <see cref="Init"/>, not <see cref="QueryCapabilities"/>.
        /// <para>
        /// <b>Why this matters:</b> a 14-bit sensor's samples still travel in a 16-bit container, but the
        /// data the SDK hands us is native (LSB-aligned) -- the consuming driver copies the delivered
        /// 16-bit words out verbatim, it does NOT left-shift to fill the container the way N.I.N.A. does
        /// on recording. So the true saturation point is 2^14-1 = 16383, not the container's 65535, and
        /// the consumer derives its full-scale ADU from this value. Reporting the container width
        /// overstated the full-scale 4x for such sensors.
        /// </para>
        /// <para>
        /// <b>Alignment caveat (needs hardware verification):</b> the above assumes native LSB-aligned
        /// delivery. QHY also exposes <see cref="CONTROL_ID.OutputDataAlignment"/>; should a camera/mode
        /// ever be found to deliver MSB-aligned (left-shifted, container-spanning) data, its delivered
        /// full-scale would be the container width again and this refinement would need to consult that
        /// control. Not acted on here without hardware to confirm the numeric convention, because
        /// guessing wrong in that direction would over-scale (clamp highlights to white) -- whereas the
        /// current native assumption only ever under-scales, which an auto-stretch recovers. The guard
        /// (positive and never exceeding the container width) means the worst case is a no-op that
        /// reproduces the previous container-width behaviour.
        /// </para>
        /// </summary>
        private void RefineAdcBitDepth()
        {
            // Reachable on the shared-handle path (the camera and CFW structs sharing one native
            // handle can both hit Init()'s already-initialized branch around connect time), so the
            // native queries are serialized under _sharedLock -- mirroring Open()'s in-lock
            // QueryCapabilities call for the identical case. The QHY SDK makes no thread-safety
            // promise for concurrent param queries on one handle.
            lock (_sharedLock)
            {
                if (_handle == IntPtr.Zero
                    || IsQHYCCDControlAvailable(_handle, CONTROL_ID.OutputDataActualBits) is not QHYCCD_SUCCESS)
                {
                    return;
                }

                var actualBits = GetQHYCCDParam(_handle, CONTROL_ID.OutputDataActualBits);
                if (!double.IsNaN(actualBits) && actualBits > 0 && actualBits <= _bitDepth)
                {
                    _adcBitDepth = (int)actualBits;
                }
            }
        }

        /// <summary>
        /// Releases this reference to the camera handle. The native handle is only closed
        /// when the last reference is released (reference counting for camera-cable CFW sharing).
        /// </summary>
        public bool Close()
        {
            if (_handle == IntPtr.Zero)
            {
                return false;
            }

            lock (_sharedLock)
            {
                if (_sharedHandles.TryGetValue(_id, out var state))
                {
                    if (state.RefCount > 1)
                    {
                        state.RefCount--;
                        _handle = IntPtr.Zero;
                        return true;
                    }

                    _sharedHandles.Remove(_id);
                }
            }

            var result = CloseQHYCCD(_handle) is QHYCCD_SUCCESS;
            _handle = IntPtr.Zero;
            return result;
        }

        private void QueryCapabilities()
        {
            // Query chip info
            if (GetQHYCCDChipInfo(_handle, out _chipWidth, out _chipHeight, out var imgW, out var imgH, out _pixelSizeX, out _pixelSizeY, out var bpp) is QHYCCD_SUCCESS)
            {
                _maxWidth = (int)imgW;
                _maxHeight = (int)imgH;
                _bitDepth = (int)bpp;
            }

            // Query capabilities
            _isColor = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_IS_COLOR) is QHYCCD_SUCCESS;
            _hasCooler = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CONTROL_COOLER) is QHYCCD_SUCCESS;
            _hasST4Port = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CONTROL_ST4PORT) is QHYCCD_SUCCESS;
            _hasMechanicalShutter = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_MECHANICALSHUTTER) is QHYCCD_SUCCESS;
            _isTriggerCamera = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_TRIGER_INTERFACE) is QHYCCD_SUCCESS;

            if (_isColor)
            {
                var bayerValue = GetQHYCCDParam(_handle, CONTROL_ID.CAM_IS_COLOR);
                if (bayerValue >= 1 && bayerValue <= 4)
                {
                    _bayerId = (BAYER_ID)(int)bayerValue;
                }
            }

            // Check USB3
            _isUSB3 = IsQHYCCDControlAvailable(_handle, CONTROL_ID.CONTROL_SPEED) is QHYCCD_SUCCESS;
        }

        // --- CFW (camera-cable filter wheel) methods ---

        /// <summary>
        /// Returns <c>true</c> if a color filter wheel is plugged into this camera's CFW port.
        /// </summary>
        public readonly bool IsCfwPlugged
            => IsQHYCCDControlAvailable(_handle, CONTROL_ID.CONTROL_CFWPORT) is QHYCCD_SUCCESS
            && IsQHYCCDCFWPlugged(_handle) is QHYCCD_SUCCESS;

        /// <summary>
        /// Gets the number of filter slots on the camera-cable CFW, or 0 if none.
        /// </summary>
        public readonly int CfwSlotCount
        {
            get
            {
                var slots = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_CFWSLOTSNUM);
                return slots > 0 ? (int)slots : 0;
            }
        }

        /// <summary>
        /// Commands the CFW to move to the given 0-based <paramref name="position"/>.
        /// Uses <see cref="SendOrder2QHYCCDCFW"/> with hex-digit encoding.
        /// </summary>
        public readonly bool SetCfwPosition(int position)
        {
            var order = position.ToString("X1");
            return SendOrder2QHYCCDCFW(_handle, order, (uint)order.Length) is QHYCCD_SUCCESS;
        }

        /// <summary>
        /// Gets the current 0-based CFW position, or -1 if the wheel is moving / status unknown.
        /// Uses <see cref="GetQHYCCDCFWStatus"/> which returns ASCII status characters.
        /// </summary>
        public readonly int GetCfwPosition()
        {
            var status = new StringBuilder(8);
            if (GetQHYCCDCFWStatus(_handle, status) is QHYCCD_SUCCESS && status.Length > 0)
            {
                var s = status.ToString();
                // "N" = moving (CFW2/CFW3), "/" = initializing (A-series)
                if (s is "N" or "/")
                {
                    return -1;
                }

                if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var pos))
                {
                    return pos;
                }
            }

            return -1;
        }

        public int MaxWidth => _maxWidth;

        public int MaxHeight => _maxHeight;

        /// <summary>
        /// The sensor's ADC resolution in bits when known (refined post-init from
        /// <see cref="CONTROL_ID.OutputDataActualBits"/>, e.g. 14 for the QHY294), otherwise the
        /// container/transfer width from <see cref="GetQHYCCDChipInfo"/> (8/16). This is the ADC
        /// resolution the <see cref="ICMOSNativeInterface"/> contract asks for -- the value a consumer
        /// turns into a full-scale ADU / max value -- NOT the transfer format selector (that stays on
        /// <see cref="_bitDepth"/>, used by <see cref="GetROIFormat"/> to pick RAW8 vs RAW16). See
        /// <see cref="RefineAdcBitDepth"/> for the native-alignment rationale.
        /// </summary>
        public int BitDepth => _adcBitDepth > 0 ? _adcBitDepth : _bitDepth;

        public double PixelSize => _pixelSizeX;

        /// <summary>
        /// System gain at the camera's CURRENT gain setting, from the camera's own calibration
        /// curve. <see cref="double.NaN"/> when it cannot be obtained.
        /// </summary>
        /// <remarks>
        /// <para><b>This used to return the constant 1.0.</b> The expression was
        /// <c>GetQHYCCDParam(CONTROL_GAIN) >= 0 ? 1.0 : 0.0</c>, which reads the gain and then
        /// discards it: every QHY frame was stamped <c>EGAIN = 1.0</c> whatever the gain, and 1.0 is
        /// a plausible enough number that nothing downstream could tell it was invented. Anything
        /// converting ADU to electrons (a read-noise estimate, a photometric error bar, a sensible
        /// weight in an integration) was silently working in ADU while believing it worked in
        /// electrons.</para>
        /// <para>NaN is the honest failure. It means "unknown", which
        /// <c>ImageMeta.ElectronsPerADU</c> already documents and every consumer already guards,
        /// whereas a fabricated number cannot be distinguished from a measurement.</para>
        /// <para><b>Unverified on hardware.</b> QHY publishes these curves in e-/ADU, which is the
        /// unit <c>EGAIN</c> wants, but the SDK manual documents the call only as "system gain curve
        /// value" without stating a unit, so the SCALE should be confirmed against a body with a
        /// published gain curve before anything trusts it quantitatively.</para>
        /// </remarks>
        public double ElectronPerADU
        {
            get
            {
                var gain = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_GAIN);
                if (!double.IsFinite(gain) || gain >= QHYCCD_ERROR)
                {
                    return double.NaN;
                }

                return QHYCCD_curveSystemGain(_handle, gain, out var systemGain) is QHYCCD_SUCCESS
                    && double.IsFinite(systemGain) && systemGain > 0
                    ? systemGain
                    : double.NaN;
            }
        }

        public bool IsTriggerCamera => _isTriggerCamera;

        public bool HasMechanicalShutter => _hasMechanicalShutter;

        public bool HasCooler => _hasCooler;

        public bool HasST4Port => _hasST4Port;

        public IReadOnlyList<int> SupportedBins
        {
            get
            {
                var bins = new List<int>(4);
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_BIN1X1MODE) is QHYCCD_SUCCESS)
                    bins.Add(1);
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_BIN2X2MODE) is QHYCCD_SUCCESS)
                    bins.Add(2);
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_BIN3X3MODE) is QHYCCD_SUCCESS)
                    bins.Add(3);
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_BIN4X4MODE) is QHYCCD_SUCCESS)
                    bins.Add(4);
                return bins;
            }
        }

        public IReadOnlyList<PixelDataFormat> SupportedPixelDataFormats
        {
            get
            {
                var formats = new List<PixelDataFormat>(3);
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_8BITS) is QHYCCD_SUCCESS)
                {
                    formats.Add(PixelDataFormat.RAW8);
                    if (_isColor)
                        formats.Add(PixelDataFormat.RGB24);
                }
                if (IsQHYCCDControlAvailable(_handle, CONTROL_ID.CAM_16BITS) is QHYCCD_SUCCESS)
                    formats.Add(PixelDataFormat.RAW16);
                return formats;
            }
        }

        public BayerPattern BayerPattern => _isColor
            ? _bayerId switch
            {
                BAYER_ID.BAYER_RG => BayerPattern.RGGB,
                BAYER_ID.BAYER_BG => BayerPattern.BGGR,
                BAYER_ID.BAYER_GR => BayerPattern.GRBG,
                BAYER_ID.BAYER_GB => BayerPattern.GBRG,
                _ => BayerPattern.Monochrome
            }
            : BayerPattern.Monochrome;

        public bool TryGetControlRange(CMOSControlType ctrlType, out int min, out int max)
        {
            min = max = 0;
            if (!DALControlTypeToQHY(ctrlType, out var qhyControl))
                return false;

            if (IsQHYCCDControlAvailable(_handle, qhyControl) is not QHYCCD_SUCCESS)
                return false;

            if (GetQHYCCDParamMinMaxStep(_handle, qhyControl, out var dMin, out var dMax, out _) is QHYCCD_SUCCESS)
            {
                min = (int)dMin;
                max = (int)dMax;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The QHY cooler is TWO controls, and neither of them is a switch.
        /// </summary>
        /// <remarks>
        /// <para>The SDK manual is explicit: <c>CONTROL_COOLER</c> is "Set cooler target temperature"
        /// and answers "Check if support AUTO cool mode", while <c>CONTROL_MANULPWM</c> is "Set cooler
        /// PWM" and answers "Check if support MANUAL cool mode". There is no on/off bit anywhere.
        /// Cooling is ENGAGED by writing a target temperature and STOPPED by writing a zero duty
        /// cycle.</para>
        /// <para><b>So mapping <c>CoolerOn</c> onto <c>CONTROL_COOLER</c>, as this did, turned every
        /// on/off into a TEMPERATURE.</b> <c>SetCoolerOn(false)</c> wrote 0 to the target and
        /// commanded a 0 C setpoint; <c>SetCoolerOn(true)</c> commanded 1 C. "Turn the cooler off" on
        /// a QHY body therefore asked it to cool HARDER, which is the one thing the session's
        /// end-of-night shutdown must never do to a warm sensor about to lose power. The read was the
        /// mirror image: <c>GetCoolerOn</c> compared the target temperature against 1 and so answered
        /// "on" only for a setpoint of exactly +1 C, which is why the shutdown step almost always
        /// skipped silently instead.</para>
        /// </remarks>
        private bool IsCoolerEngaged()
        {
            // Auto mode is engaged when the target reads back INSIDE the control's own declared
            // range. A body with nothing engaged reports a value outside it (a QHY178M reports -100
            // against a declared -50 to 100), which is the only signal the SDK offers that no
            // regulation is running; it is read as out-of-band rather than as -100 degrees so that a
            // body using a different sentinel still behaves.
            var target = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_COOLER);
            if (double.IsFinite(target) && target < QHYCCD_ERROR
                && GetQHYCCDParamMinMaxStep(_handle, CONTROL_ID.CONTROL_COOLER, out var min, out var max, out _) is QHYCCD_SUCCESS
                && target >= min && target <= max)
            {
                return true;
            }

            // Manual mode: any non-zero duty cycle is the TEC actually running.
            var pwm = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_CURPWM);
            return double.IsFinite(pwm) && pwm < QHYCCD_ERROR && pwm > 0;
        }

        public CMOSErrorCode SetControlValue(CMOSControlType controlType, int value, bool isAuto = false)
        {
            if (controlType is CMOSControlType.CoolerOn)
            {
                // OFF is unambiguous: manual mode at zero duty, which is the SDK's only way to stop
                // the TEC. ON has no command of its own, so rather than inventing a setpoint (the
                // fabrication that made the old mapping dangerous) it RESUMES the target already in
                // the control, and refuses when there is none to resume. Callers set the target
                // first in any case: ICameraDriver.CoolToSetpointAsync writes the setpoint and only
                // then turns the cooler on.
                if (value == 0)
                {
                    return ToErrorCode(SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_MANULPWM, 0));
                }

                var target = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_COOLER);
                if (double.IsFinite(target) && target < QHYCCD_ERROR
                    && GetQHYCCDParamMinMaxStep(_handle, CONTROL_ID.CONTROL_COOLER, out var min, out var max, out _) is QHYCCD_SUCCESS
                    && target >= min && target <= max)
                {
                    return ToErrorCode(SetQHYCCDParam(_handle, CONTROL_ID.CONTROL_COOLER, target));
                }

                return CMOSErrorCode.GeneralError;
            }

            if (DALControlTypeToQHY(controlType, out var qhyControl))
                return ToErrorCode(SetQHYCCDParam(_handle, qhyControl, value));

            throw new ArgumentException($"{controlType} is not supported", nameof(controlType));
        }

        public CMOSErrorCode GetControlValue(CMOSControlType controlType, out int value, out bool isAuto)
        {
            isAuto = false;
            if (controlType is CMOSControlType.CoolerOn)
            {
                value = IsCoolerEngaged() ? 1 : 0;
                return CMOSErrorCode.Success;
            }

            if (controlType is CMOSControlType.TargetTemperature)
            {
                // CONTROL_COOLER reports an OUT-OF-BAND value when no setpoint is engaged (a QHY178M
                // answers -100 against a declared range of -50 to 100), and -100 is a plausible
                // sensor temperature, so passing it through is how "nothing is engaged" became a
                // measurement. It reached SET-TEMP in the header of every frame captured with the
                // cooler idle, and the cooling ramp read it as a target it was already far past.
                // Reported as a refusal instead, which the callers already turn into "unknown".
                var target = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_COOLER);
                if (double.IsFinite(target) && target < QHYCCD_ERROR
                    && GetQHYCCDParamMinMaxStep(_handle, CONTROL_ID.CONTROL_COOLER, out var min, out var max, out _) is QHYCCD_SUCCESS
                    && target >= min && target <= max)
                {
                    value = (int)target;
                    return CMOSErrorCode.Success;
                }

                value = 0;
                return CMOSErrorCode.GeneralError;
            }

            if (DALControlTypeToQHY(controlType, out var qhyControl))
            {
                var result = GetQHYCCDParam(_handle, qhyControl);

                // GetQHYCCDParam signals failure by RETURNING the error sentinel as the value, so
                // there is no separate status to check and an unguarded read cannot tell a reading
                // from a refusal. This used to cast straight to int and answer Success: a failed
                // read became a garbage number the caller had every reason to trust, because a
                // double to int conversion of 4294967295 is out of range and lands wherever the
                // hardware puts it. Measured on a QHY178M, where nine of its twenty-seven available
                // controls answer this way, all of them set-only mode flags (the bin modes, the bit
                // depths, single-frame against live video), which are available to SET and
                // meaningless to READ.
                //
                // The range check is not belt-and-braces either: CONTROL_EXPOSURE is in microseconds
                // with a maximum of 3.6e9 on this body, so an exposure over about 35.8 minutes
                // exceeds int and would wrap. It is reported as a refusal rather than silently
                // truncated; widening the value to long would change the DAL interface and is a
                // separate decision.
                if (!double.IsFinite(result) || result >= QHYCCD_ERROR || result is > int.MaxValue or < int.MinValue)
                {
                    value = 0;
                    return CMOSErrorCode.GeneralError;
                }

                value = (int)result;
                return CMOSErrorCode.Success;
            }

            throw new ArgumentException($"{controlType} is not supported", nameof(controlType));
        }

        /// <summary>
        /// QHY's ST-4 direction codes: 0 EAST (RA+), 1 NORTH (Dec+), 2 SOUTH (Dec-), 3 WEST (RA-).
        /// </summary>
        private static uint ToQhyGuideDirection(GuideDirection direction)
            => direction switch
            {
                GuideDirection.North => 1,
                GuideDirection.South => 2,
                GuideDirection.East => 0,
                GuideDirection.West => 3,
                _ => throw new ArgumentException($"Unknown guide direction: {direction}", nameof(direction))
            };

        /// <summary>
        /// The camera times its own pulse: <c>ControlQHYCCDGuide</c> takes the duration.
        /// </summary>
        public bool CanPulseGuideForDuration => true;

        /// <inheritdoc/>
        /// <remarks>
        /// <c>ControlQHYCCDGuide</c>'s duration is in MILLISECONDS and the parameter is a
        /// <see cref="ushort"/>, so the longest pulse this hardware can be asked for is 65.535
        /// seconds. A guide correction is milliseconds to a few seconds, so the clamp is a guard
        /// against a nonsense argument rather than a real limit; it saturates instead of wrapping,
        /// because a wrapped duration would silently become a SHORT pulse in the right direction and
        /// look like a working guider that never corrects.
        /// </remarks>
        public CMOSErrorCode PulseGuideOn(GuideDirection direction, TimeSpan duration)
        {
            var milliseconds = duration.TotalMilliseconds;
            if (!double.IsFinite(milliseconds) || milliseconds < 0)
            {
                return CMOSErrorCode.GeneralError;
            }

            var clamped = (ushort)Math.Min(milliseconds, ushort.MaxValue);
            return ToErrorCode(ControlQHYCCDGuide(_handle, ToQhyGuideDirection(direction), clamped));
        }

        /// <summary>
        /// The untimed legacy form, superseded by <see cref="PulseGuideOn(GuideDirection, TimeSpan)"/>.
        /// </summary>
        /// <remarks>
        /// <b>There is no way to start an st-4 pulse on this hardware without stating how long it
        /// runs</b>, so this asks for the longest one it can express and relies on the caller to stop
        /// it, which is what it has always done (it passed 50000, a 50 SECOND pulse, while the caller
        /// believed it was asking for milliseconds). Any consumer that checks
        /// <see cref="CanPulseGuideForDuration"/> takes the timed overload and never reaches this.
        /// </remarks>
        public CMOSErrorCode PulseGuideOn(GuideDirection direction)
            => ToErrorCode(ControlQHYCCDGuide(_handle, ToQhyGuideDirection(direction), ushort.MaxValue));

        /// <summary>
        /// Cannot stop a pulse: the SDK exposes no cancel, and the camera runs the duration it was
        /// given.
        /// </summary>
        /// <remarks>
        /// Reporting success here is the honest answer only because <see cref="CanPulseGuideForDuration"/>
        /// is true, so a caller states the duration up front and never needs this to end a pulse. It
        /// was NOT honest before: the duration was a hardcoded constant, this was the only thing that
        /// could have ended it, and it did nothing while saying it had worked.
        /// </remarks>
        public CMOSErrorCode PulseGuideOff(GuideDirection direction) => CMOSErrorCode.Success;

        public CMOSErrorCode StartLightExposure() => StartExposureCore();

        public CMOSErrorCode StartDarkExposure()
        {
            if (_hasMechanicalShutter)
                ControlQHYCCDShutter(_handle, 1); // close shutter

            return StartExposureCore();
        }

        private CMOSErrorCode StartExposureCore()
        {
            SetQHYCCDStreamMode(_handle, 0); // single frame mode

            // Capture the exposure length up front (it was set just before this call) so
            // GetExposureStatus can apply the read-directly readout-timing rule below.
            _exposureMicros = GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_EXPOSURE);
            _exposureStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            var ret = ExpQHYCCDSingleFrame(_handle);

            // QHYCCD_READ_DIRECTLY is NOT a failure: the camera reads the frame out
            // directly rather than through a separate exposing/readout phase. Only
            // QHYCCD_ERROR is a genuine failure -- treat everything else as a started
            // exposure (the previous ToErrorCode() mapped READ_DIRECTLY to GeneralError,
            // which made the downstream driver throw and such cameras unusable).
            _readDirectly = ret == QHYCCD_READ_DIRECTLY;
            return ret == QHYCCD_ERROR ? CMOSErrorCode.GeneralError : CMOSErrorCode.Success;
        }

        public CMOSErrorCode StopExposure() => ToErrorCode(CancelQHYCCDExposingAndReadout(_handle));

        public CMOSErrorCode GetExposureStatus(out ExposureStatus exposureStatus)
        {
            if (_readDirectly)
            {
                // Read-directly cameras don't report a meaningful GetQHYCCDExposureRemaining,
                // so gate readout on elapsed time per QHY's October 2024 guidance: read
                // immediately for sub-3s exposures, or after 2s for longer ones. The
                // subsequent (blocking) GetQHYCCDSingleFrame then returns the frame.
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_exposureStartTicks);
                exposureStatus = IsReadDirectlyFrameReady(elapsed, _exposureMicros)
                    ? ExposureStatus.Success
                    : ExposureStatus.Working;
                return CMOSErrorCode.Success;
            }

            var remaining = GetQHYCCDExposureRemaining(_handle);
            if (remaining <= ExposureRemainingDoneMicros)
            {
                exposureStatus = ExposureStatus.Success;
            }
            else if (remaining == uint.MaxValue)
            {
                exposureStatus = ExposureStatus.Failed;
            }
            else
            {
                exposureStatus = ExposureStatus.Working;
            }

            return CMOSErrorCode.Success;
        }

        public CMOSErrorCode GetStartPosition(out int startX, out int startY)
        {
            if (GetQHYCCDCurrentROI(_handle, out var sx, out var sy, out _, out _) is QHYCCD_SUCCESS)
            {
                startX = (int)sx;
                startY = (int)sy;
                return CMOSErrorCode.Success;
            }

            startX = startY = 0;
            return CMOSErrorCode.GeneralError;
        }

        public CMOSErrorCode SetStartPosition(int startX, int startY)
        {
            // QHY sets start position as part of SetQHYCCDResolution
            // Get current size first
            if (GetQHYCCDCurrentROI(_handle, out _, out _, out var sizeX, out var sizeY) is QHYCCD_SUCCESS)
                return ToErrorCode(SetQHYCCDResolution(_handle, (uint)startX, (uint)startY, sizeX, sizeY));

            return CMOSErrorCode.GeneralError;
        }

        public CMOSErrorCode GetROIFormat(out int width, out int height, out int bin, out PixelDataFormat pixelDataFormat)
        {
            if (GetQHYCCDCurrentROI(_handle, out _, out _, out var w, out var h) is QHYCCD_SUCCESS)
            {
                width = (int)w;
                height = (int)h;
                bin = 1; // QHY manages bin separately
                pixelDataFormat = _bitDepth > 8 ? PixelDataFormat.RAW16 : PixelDataFormat.RAW8;
                return CMOSErrorCode.Success;
            }

            width = height = bin = 0;
            pixelDataFormat = PixelDataFormat.RAW8;
            return CMOSErrorCode.GeneralError;
        }

        public CMOSErrorCode SetROIFormat(int width, int height, int bin, PixelDataFormat pixelDataFormat)
        {
            SetQHYCCDBinMode(_handle, (uint)bin, (uint)bin);
            SetQHYCCDBitsMode(_handle, pixelDataFormat is PixelDataFormat.RAW16 ? 16u : 8u);
            return ToErrorCode(SetQHYCCDResolution(_handle, 0, 0, (uint)width, (uint)height));
        }

        public CMOSErrorCode GetDataAfterExposure(IntPtr buffer, int bufferSize)
        {
            return ToErrorCode(GetQHYCCDSingleFrame(_handle, out _, out _, out _, out _, buffer));
        }

        public bool TryGetEffectiveArea(out int startX, out int startY, out int width, out int height)
            => TryGetArea(GetQHYCCDEffectiveArea(_handle, out var x, out var y, out var w, out var h), x, y, w, h,
                out startX, out startY, out width, out height);

        public bool TryGetOverscanArea(out int startX, out int startY, out int width, out int height)
            => TryGetArea(GetQHYCCDOverScanArea(_handle, out var x, out var y, out var w, out var h), x, y, w, h,
                out startX, out startY, out width, out height);

        /// <summary>
        /// Shared guard for the two area queries: a SUCCESS carrying a zero or oversized extent is
        /// not an area, and is reported as "declares none" rather than passed on.
        /// </summary>
        /// <remarks>
        /// A QHY178M answers SUCCESS for both while reporting an effective area equal to the whole
        /// readout and an overscan of 0 x 0, so an empty rectangle is a NORMAL answer from a body
        /// with no shielded margin and must not reach a caller as a degenerate section. The int cast
        /// is guarded for the same reason the control read is: these are uint out-parameters, and a
        /// firmware that fills them with a sentinel would otherwise become a negative rectangle.
        /// </remarks>
        private static bool TryGetArea(uint result, uint x, uint y, uint w, uint h,
            out int startX, out int startY, out int width, out int height)
        {
            startX = startY = width = height = 0;
            if (result is not QHYCCD_SUCCESS || w is 0 || h is 0
                || x > int.MaxValue || y > int.MaxValue || w > int.MaxValue || h > int.MaxValue)
            {
                return false;
            }

            startX = (int)x;
            startY = (int)y;
            width = (int)w;
            height = (int)h;
            return true;
        }

        private static string GetModelFromId(string id)
        {
            // QHY camera IDs are in the format "MODEL-SERIAL", e.g. "QHY600M-abc123"
            var dashIndex = id.LastIndexOf('-');
            return dashIndex > 0 ? id[..dashIndex] : id;
        }
    }

    public static bool DALControlTypeToQHY(CMOSControlType dalValue, out CONTROL_ID qhyValue)
    {
        qhyValue = dalValue switch
        {
            CMOSControlType.Gain => CONTROL_ID.CONTROL_GAIN,
            CMOSControlType.Exposure => CONTROL_ID.CONTROL_EXPOSURE,
            CMOSControlType.Gamma => CONTROL_ID.CONTROL_GAMMA,
            CMOSControlType.WB_R => CONTROL_ID.CONTROL_WBR,
            CMOSControlType.WB_B => CONTROL_ID.CONTROL_WBB,
            CMOSControlType.Brightness => CONTROL_ID.CONTROL_BRIGHTNESS,
            CMOSControlType.BandwidthOverload => CONTROL_ID.CONTROL_USBTRAFFIC,
            CMOSControlType.Overclock => CONTROL_ID.CONTROL_SPEED,
            CMOSControlType.TemperatureDeci => CONTROL_ID.CONTROL_CURTEMP,
            CMOSControlType.Flip => (CONTROL_ID)int.MaxValue, // not directly supported
            CMOSControlType.AutoMaxGain => (CONTROL_ID)int.MaxValue,
            CMOSControlType.AutoMaxExposure => (CONTROL_ID)int.MaxValue,
            CMOSControlType.AutoMaxBrightness => (CONTROL_ID)int.MaxValue,
            CMOSControlType.HardwareBin => (CONTROL_ID)int.MaxValue,
            CMOSControlType.HighSpeedMode => CONTROL_ID.CONTROL_SPEED,
            CMOSControlType.CoolerPowerPercent => CONTROL_ID.CONTROL_CURPWM,
            CMOSControlType.TargetTemperature => CONTROL_ID.CONTROL_COOLER,
            // CoolerOn is deliberately NOT in this table: QHYCamera.SetControlValue and
            // GetControlValue handle it directly, because QHY has no on/off bit and routing it
            // through here is what made "turn the cooler off" command a 0 C setpoint.
            CMOSControlType.CoolerOn => (CONTROL_ID)int.MaxValue,
            CMOSControlType.MonoBin => (CONTROL_ID)int.MaxValue,
            CMOSControlType.FanOn => (CONTROL_ID)int.MaxValue,
            CMOSControlType.PatternAdjust => (CONTROL_ID)int.MaxValue,
            CMOSControlType.AntiDewHeater => (CONTROL_ID)int.MaxValue,
            CMOSControlType.Humidity => CONTROL_ID.CAM_HUMIDITY,
            CMOSControlType.EnableDDR => CONTROL_ID.CONTROL_DDR,
            _ => (CONTROL_ID)int.MaxValue
        };

        return (int)qhyValue is not int.MaxValue;
    }

    private static CMOSErrorCode ToErrorCode(uint qhyResult)
    {
        return qhyResult switch
        {
            QHYCCD_SUCCESS => CMOSErrorCode.Success,
            QHYCCD_ERROR => CMOSErrorCode.GeneralError,
            _ => CMOSErrorCode.GeneralError
        };
    }

    public enum CONTROL_ID
    {
        CONTROL_BRIGHTNESS = 0,
        CONTROL_CONTRAST,
        CONTROL_WBR,
        CONTROL_WBB,
        CONTROL_WBG,
        CONTROL_GAMMA,
        CONTROL_GAIN,
        CONTROL_OFFSET,
        CONTROL_EXPOSURE,
        CONTROL_SPEED,
        CONTROL_TRANSFERBIT,
        CONTROL_CHANNELS,
        CONTROL_USBTRAFFIC,
        CONTROL_ROWNOISERE,
        CONTROL_CURTEMP,
        CONTROL_CURPWM,
        CONTROL_MANULPWM,
        CONTROL_CFWPORT,
        CONTROL_COOLER,
        CONTROL_ST4PORT,
        CAM_COLOR,
        CAM_BIN1X1MODE,
        CAM_BIN2X2MODE,
        CAM_BIN3X3MODE,
        CAM_BIN4X4MODE,
        CAM_MECHANICALSHUTTER,
        CAM_TRIGER_INTERFACE,
        CAM_TECOVERPROTECT_INTERFACE,
        CAM_SINGNALCLAMP_INTERFACE,
        CAM_FINETONE_INTERFACE,
        CAM_SHUTTERMOTORHEATING_INTERFACE,
        CAM_CALIBRATEFPN_INTERFACE,
        CAM_CHIPTEMPERATURESENSOR_INTERFACE,
        CAM_USBREADOUTSLOWEST_INTERFACE,
        CAM_8BITS,
        CAM_16BITS,
        CAM_GPS,
        CAM_IGNOREOVERSCAN_INTERFACE,
        QHYCCD_3A_AUTOBALANCE = 38,
        QHYCCD_3A_AUTOEXPOSURE = 39,
        QHYCCD_3A_AUTOFOCUS,
        CONTROL_AMPV,
        CONTROL_VCAM,
        CAM_VIEW_MODE,
        CONTROL_CFWSLOTSNUM,
        IS_EXPOSING_DONE,
        ScreenStretchB,
        ScreenStretchW,
        CONTROL_DDR,
        CAM_LIGHT_PERFORMANCE_MODE,
        CAM_QHY5II_GUIDE_MODE,
        DDR_BUFFER_CAPACITY,
        DDR_BUFFER_READ_THRESHOLD,
        DefaultGain,
        DefaultOffset,
        OutputDataActualBits,
        OutputDataAlignment,
        CAM_SINGLEFRAMEMODE,
        CAM_LIVEVIDEOMODE,
        CAM_IS_COLOR,
        hasHardwareFrameCounter,
        CONTROL_MAX_ID_Error,
        CAM_HUMIDITY,
        CAM_PRESSURE,
        CONTROL_VACUUM_PUMP,
        CONTROL_SensorChamberCycle_PUMP,
        CAM_32BITS,
        CAM_Sensor_ULVO_Status,
        CAM_SensorPhaseReTrain,
        CAM_InitConfigFromFlash,
        CAM_TRIGER_MODE,
        CAM_TRIGER_OUT,
        CAM_BURST_MODE,
        CAM_SPEAKER_LED_ALARM,
        CAM_WATCH_DOG_FPGA,
        CAM_BIN6X6MODE,
        CAM_BIN8X8MODE,
    }

    public enum BAYER_ID
    {
        BAYER_GB = 1,
        BAYER_GR,
        BAYER_BG,
        BAYER_RG
    }

    // Public because every P/Invoke on this class returns one of these, so a caller outside it
    // cannot read its own result without them. They were private, which left a consumer either
    // comparing against a bare 0 or redeclaring the success code beside its own call site, and a
    // duplicated constant is exactly the kind that drifts from the one it was copied from.
    public const uint QHYCCD_SUCCESS = 0;
    public const uint QHYCCD_ERROR = 0xFFFFFFFF;

    // ExpQHYCCDSingleFrame returns this (rather than QHYCCD_SUCCESS) for cameras that
    // read the frame out directly instead of via a separate exposing/readout phase.
    // It is NOT an error. See QHYCCD_CAMERA_INFO.StartExposureCore / GetExposureStatus.
    const uint QHYCCD_READ_DIRECTLY = 0x2001;

    // GetQHYCCDExposureRemaining reports the remaining exposure time in microseconds
    // (0xFFFFFFFF/uint.MaxValue signals an error). Within ~100us of the end the frame
    // is effectively done, so treat that as complete rather than polling for an exact
    // zero we may never land on.
    const uint ExposureRemainingDoneMicros = 100;

    // QHY's October 2024 read-directly readout-timing rule (mirrors PHD2 cam_qhy.cpp):
    // GetQHYCCDSingleFrame may be called immediately for exposures under 3s; for longer
    // exposures wait 2s first. Exposed as a pure predicate for clarity/testability.
    const double ReadDirectlyLongExposureMicros = 3_000_000d;
    static readonly TimeSpan ReadDirectlyLongExposureDelay = TimeSpan.FromSeconds(2);

    internal static bool IsReadDirectlyFrameReady(TimeSpan elapsed, double exposureMicros)
        => exposureMicros < ReadDirectlyLongExposureMicros || elapsed >= ReadDirectlyLongExposureDelay;

    const string QHYSharedLib = "qhyccd";

    // --- Resource management ---

    [LibraryImport(QHYSharedLib, EntryPoint = "InitQHYCCDResource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint InitQHYCCDResource();

    [LibraryImport(QHYSharedLib, EntryPoint = "ReleaseQHYCCDResource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ReleaseQHYCCDResource();

    // --- Scanning ---

    [LibraryImport(QHYSharedLib, EntryPoint = "ScanQHYCCD")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ScanQHYCCD();

    [DllImport(QHYSharedLib, EntryPoint = "GetQHYCCDId", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetQHYCCDId(uint index, [MarshalAs(UnmanagedType.LPStr)] StringBuilder id);

    [DllImport(QHYSharedLib, EntryPoint = "GetQHYCCDModel", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetQHYCCDModel([MarshalAs(UnmanagedType.LPStr)] string id, [MarshalAs(UnmanagedType.LPStr)] StringBuilder model);

    // --- Open/Close ---

    [LibraryImport(QHYSharedLib, EntryPoint = "OpenQHYCCD")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial IntPtr OpenQHYCCD(IntPtr id);

    [LibraryImport(QHYSharedLib, EntryPoint = "CloseQHYCCD")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint CloseQHYCCD(IntPtr handle);

    // --- Init/Stream ---

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDStreamMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDStreamMode(IntPtr handle, byte mode);

    [LibraryImport(QHYSharedLib, EntryPoint = "InitQHYCCD")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint InitQHYCCD(IntPtr handle);

    // --- Control ---

    [LibraryImport(QHYSharedLib, EntryPoint = "IsQHYCCDControlAvailable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint IsQHYCCDControlAvailable(IntPtr handle, CONTROL_ID controlId);

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDParam")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDParam(IntPtr handle, CONTROL_ID controlId, double value);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDParam")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial double GetQHYCCDParam(IntPtr handle, CONTROL_ID controlId);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDParamMinMaxStep")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDParamMinMaxStep(IntPtr handle, CONTROL_ID controlId, out double min, out double max, out double step);

    /// <summary>
    /// The system gain the camera's own calibration curve gives for a SDK gain value, which is what
    /// <see cref="QHYCamera.ElectronPerADU"/> reports.
    /// </summary>
    [LibraryImport(QHYSharedLib, EntryPoint = "QHYCCD_curveSystemGain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint QHYCCD_curveSystemGain(IntPtr handle, double gainV, out double systemGain);

    // --- Chip Info ---

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDChipInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDChipInfo(IntPtr handle, out double chipW, out double chipH, out uint imageW, out uint imageH, out double pixelW, out double pixelH, out uint bpp);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDEffectiveArea")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDEffectiveArea(IntPtr handle, out uint startX, out uint startY, out uint sizeX, out uint sizeY);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDOverScanArea")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDOverScanArea(IntPtr handle, out uint startX, out uint startY, out uint sizeX, out uint sizeY);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDCurrentROI")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDCurrentROI(IntPtr handle, out uint startX, out uint startY, out uint sizeX, out uint sizeY);

    // --- Resolution/Bin/Bits ---

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDResolution")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDResolution(IntPtr handle, uint x, uint y, uint xSize, uint ySize);

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDBinMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDBinMode(IntPtr handle, uint wBin, uint hBin);

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDBitsMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDBitsMode(IntPtr handle, uint bits);

    // --- Exposure ---

    [LibraryImport(QHYSharedLib, EntryPoint = "ExpQHYCCDSingleFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ExpQHYCCDSingleFrame(IntPtr handle);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDSingleFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDSingleFrame(IntPtr handle, out uint w, out uint h, out uint bpp, out uint channels, IntPtr imgData);

    [LibraryImport(QHYSharedLib, EntryPoint = "CancelQHYCCDExposing")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint CancelQHYCCDExposing(IntPtr handle);

    [LibraryImport(QHYSharedLib, EntryPoint = "CancelQHYCCDExposingAndReadout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint CancelQHYCCDExposingAndReadout(IntPtr handle);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDExposureRemaining")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDExposureRemaining(IntPtr handle);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDMemLength")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDMemLength(IntPtr handle);

    // --- Live Mode ---

    [LibraryImport(QHYSharedLib, EntryPoint = "BeginQHYCCDLive")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint BeginQHYCCDLive(IntPtr handle);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDLiveFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDLiveFrame(IntPtr handle, out uint w, out uint h, out uint bpp, out uint channels, IntPtr imgData);

    [LibraryImport(QHYSharedLib, EntryPoint = "StopQHYCCDLive")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint StopQHYCCDLive(IntPtr handle);

    // --- Guide ---

    [LibraryImport(QHYSharedLib, EntryPoint = "ControlQHYCCDGuide")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ControlQHYCCDGuide(IntPtr handle, uint direction, ushort duration);

    // --- Temperature ---

    [LibraryImport(QHYSharedLib, EntryPoint = "ControlQHYCCDTemp")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ControlQHYCCDTemp(IntPtr handle, double targetTemp);

    // --- Shutter ---

    [LibraryImport(QHYSharedLib, EntryPoint = "ControlQHYCCDShutter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint ControlQHYCCDShutter(IntPtr handle, byte status);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDShutterStatus")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDShutterStatus(IntPtr handle);

    // --- CFW (Color Filter Wheel) ---

    [DllImport(QHYSharedLib, EntryPoint = "SendOrder2QHYCCDCFW", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SendOrder2QHYCCDCFW(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string order, uint length);

    [DllImport(QHYSharedLib, EntryPoint = "GetQHYCCDCFWStatus", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetQHYCCDCFWStatus(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] StringBuilder status);

    [LibraryImport(QHYSharedLib, EntryPoint = "IsQHYCCDCFWPlugged")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint IsQHYCCDCFWPlugged(IntPtr handle);

    // --- Humidity/Pressure ---

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDHumidity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDHumidity(IntPtr handle, out double humidity);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDPressure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDPressure(IntPtr handle, out double pressure);

    // --- FW Version ---

    [DllImport(QHYSharedLib, EntryPoint = "GetQHYCCDFWVersion", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetQHYCCDFWVersion(IntPtr handle, [MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] buf);

    // --- SDK Version ---

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDSDKVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDSDKVersion(out uint year, out uint month, out uint day, out uint subday);

    public static Version GetSDKVersion()
    {
        if (GetQHYCCDSDKVersion(out var year, out var month, out var day, out var subday) is QHYCCD_SUCCESS)
            return new Version((int)year, (int)month, (int)day, (int)subday);
        return new Version();
    }

    // --- Read Mode ---

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDNumberOfReadModes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDNumberOfReadModes(IntPtr handle, out uint numModes);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDReadModeResolution")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDReadModeResolution(IntPtr handle, uint modeNumber, out uint width, out uint height);

    [DllImport(QHYSharedLib, EntryPoint = "GetQHYCCDReadModeName", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetQHYCCDReadModeName(IntPtr handle, uint modeNumber, [MarshalAs(UnmanagedType.LPStr)] StringBuilder name);

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDReadMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDReadMode(IntPtr handle, uint modeNumber);

    [LibraryImport(QHYSharedLib, EntryPoint = "GetQHYCCDReadMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint GetQHYCCDReadMode(IntPtr handle, out uint modeNumber);

    // --- Debayer ---

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDDebayerOnOff")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDDebayerOnOff(IntPtr handle, [MarshalAs(UnmanagedType.I1)] bool onOff);

    // --- Timeout ---

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDSingleFrameTimeOut")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial uint SetQHYCCDSingleFrameTimeOut(IntPtr handle, uint time);

    // --- Logging ---

    [LibraryImport(QHYSharedLib, EntryPoint = "EnableQHYCCDMessage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void EnableQHYCCDMessage([MarshalAs(UnmanagedType.I1)] bool enable);

    [LibraryImport(QHYSharedLib, EntryPoint = "EnableQHYCCDLogFile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void EnableQHYCCDLogFile([MarshalAs(UnmanagedType.I1)] bool enable);

    [LibraryImport(QHYSharedLib, EntryPoint = "SetQHYCCDLogLevel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    public static partial void SetQHYCCDLogLevel(byte logLevel);
}
