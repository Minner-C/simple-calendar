using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using NAudio.CoreAudioApi;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 系统音量与屏幕亮度读取/设置辅助类
/// 音量：通过 NAudio (Core Audio API 封装) 读取与设置主输出端点音量
/// 亮度：通过 WMI Win32_WmiMonitorBrightness 读取，通过 WmiMonitorBrightnessMethods 设置
/// </summary>
public static class VolumeBrightnessHelper
{
    // ===== 音量（NAudio Core Audio API 封装） =====

    private static MMDeviceEnumerator? _enumerator;
    private static MMDevice? _cachedDevice;
    private static readonly object _lock = new();

    /// <summary>获取默认音频输出设备（缓存），失败返回 null</summary>
    private static MMDevice? GetDefaultDevice()
    {
        // 已缓存则直接返回
        if (_cachedDevice != null) return _cachedDevice;
        lock (_lock)
        {
            if (_cachedDevice != null) return _cachedDevice;
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                // DataFlow.Render = 输出设备，Role.Console = 默认角色
                _cachedDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                return _cachedDevice;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Volume] GetDefaultDevice 异常: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>读取当前主音量（0-100），失败返回 -1</summary>
    public static float GetVolume()
    {
        try
        {
            var device = GetDefaultDevice();
            if (device == null) return -1;
            // NAudio 返回 0.0-1.0 的标量值
            float level = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            return level * 100f;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Volume] GetVolume 异常: {ex.Message}");
            return -1;
        }
    }

    /// <summary>设置主音量（0-100），成功返回 true</summary>
    public static bool SetVolume(float percent)
    {
        try
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            var device = GetDefaultDevice();
            if (device == null) return false;
            // 先取消静音（设置音量时如果系统是静音的，可能听不到声音）
            device.AudioEndpointVolume.Mute = false;
            // NAudio 期望 0.0-1.0 的标量值
            device.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Volume] SetVolume 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>获取静音状态</summary>
    public static bool IsMuted()
    {
        try
        {
            var device = GetDefaultDevice();
            if (device == null) return false;
            return device.AudioEndpointVolume.Mute;
        }
        catch { return false; }
    }

    // ===== 屏幕亮度（WMI） =====

    /// <summary>读取当前屏幕亮度（0-100），失败返回 -1</summary>
    public static float GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness");
            foreach (var mo in searcher.Get())
            {
                // CurrentBrightness 为 byte 类型
                return Convert.ToSingle(mo["CurrentBrightness"]);
            }
            return -1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Brightness] GetBrightness 异常: {ex.Message}");
            return -1;
        }
    }

    /// <summary>设置屏幕亮度（0-100），失败返回 false</summary>
    public static bool SetBrightness(float percent)
    {
        try
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            byte b = (byte)Math.Round(percent);
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get().Cast<ManagementObject>())
            {
                using (mo)
                {
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, b });
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Brightness] SetBrightness 异常: {ex.Message}");
            return false;
        }
    }
}
