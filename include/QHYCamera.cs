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
            lock (_sharedLock)
            {
                if (_sharedHandles.TryGetValue(_id, out var state) && state.Initialized)
                {
                    return true;
                }
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
            }

            return result;
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

        public int BitDepth => _bitDepth;

        public double PixelSize => _pixelSizeX;

        public double ElectronPerADU => GetQHYCCDParam(_handle, CONTROL_ID.CONTROL_GAIN) >= 0 ? 1.0 : 0.0;

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

        public CMOSErrorCode SetControlValue(CMOSControlType controlType, int value, bool isAuto = false)
        {
            if (DALControlTypeToQHY(controlType, out var qhyControl))
                return ToErrorCode(SetQHYCCDParam(_handle, qhyControl, value));

            throw new ArgumentException($"{controlType} is not supported", nameof(controlType));
        }

        public CMOSErrorCode GetControlValue(CMOSControlType controlType, out int value, out bool isAuto)
        {
            isAuto = false;
            if (DALControlTypeToQHY(controlType, out var qhyControl))
            {
                var result = GetQHYCCDParam(_handle, qhyControl);
                value = (int)result;
                return CMOSErrorCode.Success;
            }

            throw new ArgumentException($"{controlType} is not supported", nameof(controlType));
        }

        public CMOSErrorCode PulseGuideOn(GuideDirection direction)
        {
            uint qhyDir = direction switch
            {
                GuideDirection.North => 1,
                GuideDirection.South => 2,
                GuideDirection.East => 0,
                GuideDirection.West => 3,
                _ => throw new ArgumentException($"Unknown guide direction: {direction}", nameof(direction))
            };

            return ToErrorCode(ControlQHYCCDGuide(_handle, qhyDir, 50000));
        }

        public CMOSErrorCode PulseGuideOff(GuideDirection direction) => CMOSErrorCode.Success;

        public CMOSErrorCode StartLightExposure()
        {
            SetQHYCCDStreamMode(_handle, 0); // single frame mode
            return ToErrorCode(ExpQHYCCDSingleFrame(_handle));
        }

        public CMOSErrorCode StartDarkExposure()
        {
            if (_hasMechanicalShutter)
                ControlQHYCCDShutter(_handle, 1); // close shutter

            SetQHYCCDStreamMode(_handle, 0);
            return ToErrorCode(ExpQHYCCDSingleFrame(_handle));
        }

        public CMOSErrorCode StopExposure() => ToErrorCode(CancelQHYCCDExposingAndReadout(_handle));

        public CMOSErrorCode GetExposureStatus(out ExposureStatus exposureStatus)
        {
            var remaining = GetQHYCCDExposureRemaining(_handle);
            if (remaining == 0 || remaining <= 100)
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
            CMOSControlType.CoolerOn => CONTROL_ID.CONTROL_COOLER,
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

    const uint QHYCCD_SUCCESS = 0;
    const uint QHYCCD_ERROR = 0xFFFFFFFF;

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
