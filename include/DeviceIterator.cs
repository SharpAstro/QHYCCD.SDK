using System.Text;
using TianWen.DAL;
using static QHYCCD.SDK.QHYCamera;

namespace QHYCCD.SDK;

public class DeviceIterator<TDeviceInfo> : NativeDeviceIteratorBase<TDeviceInfo>
    where TDeviceInfo : struct, INativeDeviceInfo
{
    protected override int DeviceCount()
    {
        if (typeof(TDeviceInfo) == typeof(QHYCCD_CAMERA_INFO))
            return (int)ScanQHYCCD();

        return 0;
    }

    protected override TDeviceInfo? GetDeviceInfo(int index)
    {
        if (typeof(TDeviceInfo) == typeof(QHYCCD_CAMERA_INFO))
        {
            var id = new StringBuilder(64);
            if (GetQHYCCDId((uint)index, id) is 0)
            {
                var camInfo = new QHYCCD_CAMERA_INFO(id.ToString());
                return (TDeviceInfo)(INativeDeviceInfo)camInfo;
            }
        }

        return null;
    }
}
