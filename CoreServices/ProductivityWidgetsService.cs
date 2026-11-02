using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Geolocation;

namespace SkyBar.CoreServices;

public record SystemStatsSnapshot(
    int RamUsagePercent,
    int BatteryPercent,
    bool IsCharging,
    int CpuUsagePercent = 0,
    string DownloadSpeedText = "0.0 KB/s",
    string UploadSpeedText = "0.0 KB/s",
    string AudioDeviceName = "System Audio Output",
    double UsedRamGb = 0,
    double TotalRamGb = 0);

public record HourlyWeatherSlot(
    string TimeLabel,
    string IconKind, // "Sun", "PartlyCloudy", "Cloudy", "Rain", "Night"
    int TempCelsius,
    int RainChancePercent = 0);

public record WeatherSnapshot(
    int TempCelsius,
    string Condition,
    string Location,
    int HumidityPercent,
    int WindKmh,
    bool IsDaytime = true,
    int FeelsLikeCelsius = 31,
    string NaturalSummary = "Cuaca cukup bersahabat hari ini, waktu yang pas buat fokus beraktivitas.",
    int HighCelsius = 32,
    int LowCelsius = 24,
    int UvIndex = 2,
    string UvLevelText = "Low",
    string SunriseTimeText = "05:50",
    string SunsetTimeText = "17:55",
    string IconKind = "PartlyCloudy",
    IReadOnlyList<HourlyWeatherSlot>? HourlySlots = null,
    double Latitude = -6.2088,
    double Longitude = 106.8456);

public sealed class ProductivityWidgetsService
{
    private readonly DispatcherTimer _pomodoroTimer;
    private TimeSpan _remainingTime = TimeSpan.FromMinutes(25);
    public bool IsPomodoroRunning { get; private set; }
    public bool IsBreakSession { get; private set; }
    public event Action<string, bool>? PomodoroTick;

    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _weatherTimer;
    public event Action<SystemStatsSnapshot>? SystemStatsUpdated;
    public event Action<WeatherSnapshot>? WeatherUpdated;

    private long _prevBytesReceived;
    private long _prevBytesSent;
    private DateTime _prevNetSampleTime = DateTime.MinValue;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(7) };

    public WeatherSnapshot LatestWeather { get; private set; } = CreateDefaultWeather();

    public ProductivityWidgetsService()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SkyBarWindows/1.0.0");

        _pomodoroTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pomodoroTimer.Tick += OnPomodoroTick;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
        _statsTimer.Tick += (_, _) => PublishSystemStats();
        _statsTimer.Start();
        PublishSystemStats();

        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        _weatherTimer.Start();
        _ = RefreshWeatherAsync();
    }

    private static WeatherSnapshot CreateDefaultWeather()
    {
        int hour = DateTime.Now.Hour;
        bool isDay = hour is >= 6 and < 18;
        var slots = new List<HourlyWeatherSlot>();
        for (int i = 0; i < 6; i++)
        {
            int h = (hour + i) % 24;
            bool slotDay = h is >= 6 and < 18;
            slots.Add(new HourlyWeatherSlot(
                i == 0 ? "Now" : $"{h:00}:00",
                slotDay ? "PartlyCloudy" : "Night",
                30 - (i % 3)));
        }

        return new WeatherSnapshot(
            30,
            "Partly Cloudy",
            "Lokasi Laptop",
            68,
            10,
            isDay,
            32,
            "Kondisi berawan teduh beberapa jam ke depan, udara nyaman untuk beraktivitas.",
            32,
            24,
            isDay ? 3 : 0,
            isDay ? "Moderate" : "Low",
            "05:48",
            "17:54",
            isDay ? "PartlyCloudy" : "Night",
            slots);
    }

    public void TogglePomodoro()
    {
        IsPomodoroRunning = !IsPomodoroRunning;
        if (IsPomodoroRunning) _pomodoroTimer.Start(); else _pomodoroTimer.Stop();
        PomodoroTick?.Invoke(_remainingTime.ToString(@"mm\:ss"), false);
    }

    public void ResetPomodoro(int minutes = 25, bool isBreak = false)
    {
        _pomodoroTimer.Stop();
        IsPomodoroRunning = false;
        IsBreakSession = isBreak;
        _remainingTime = TimeSpan.FromMinutes(minutes);
        PomodoroTick?.Invoke(_remainingTime.ToString(@"mm\:ss"), false);
    }

    private void OnPomodoroTick(object? sender, EventArgs e)
    {
        if (_remainingTime.TotalSeconds > 0)
        {
            _remainingTime = _remainingTime.Subtract(TimeSpan.FromSeconds(1));
            PomodoroTick?.Invoke(_remainingTime.ToString(@"mm\:ss"), false);
        }
        else
        {
            _pomodoroTimer.Stop();
            IsPomodoroRunning = false;
            IsBreakSession = !IsBreakSession;
            _remainingTime = TimeSpan.FromMinutes(IsBreakSession ? 5 : 25);
            PomodoroTick?.Invoke(IsBreakSession ? "Istirahat 05:00" : "Fokus 25:00", true);
        }
    }

    // ================================================================
    //  SINKRONISASI LOKASI LAPTOP & CUACA GAYA MACOS WEATHER
    // ================================================================

    public async Task RefreshWeatherAsync()
    {
        try
        {
            (double lat, double lon, string city) = await ResolveLaptopLocationAsync();

            string latStr = lat.ToString("0.0000", CultureInfo.InvariantCulture);
            string lonStr = lon.ToString("0.0000", CultureInfo.InvariantCulture);
            string url =
                $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
                "&hourly=temperature_2m,weather_code,precipitation_probability" +
                "&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset,uv_index_max" +
                "&forecast_days=2&timezone=auto";

            string json = await Http.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var current = root.GetProperty("current");

            int tempC = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
            int feelsC = (int)Math.Round(current.GetProperty("apparent_temperature").GetDouble());
            int humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            int windKmh = (int)Math.Round(current.GetProperty("wind_speed_10m").GetDouble());
            bool isDay = current.GetProperty("is_day").GetInt32() == 1;
            int wmoCode = current.GetProperty("weather_code").GetInt32();

            int highC = tempC + 2;
            int lowC = tempC - 4;
            int uvIndex = isDay ? 3 : 0;
            string sunriseText = "05:50";
            string sunsetText = "17:55";

            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var maxArr) && maxArr.GetArrayLength() > 0)
                    highC = (int)Math.Round(maxArr[0].GetDouble());
                if (daily.TryGetProperty("temperature_2m_min", out var minArr) && minArr.GetArrayLength() > 0)
                    lowC = (int)Math.Round(minArr[0].GetDouble());
                if (daily.TryGetProperty("uv_index_max", out var uvArr) && uvArr.GetArrayLength() > 0)
                    uvIndex = isDay ? (int)Math.Round(uvArr[0].GetDouble()) : 0;
                if (daily.TryGetProperty("sunrise", out var srArr) && srArr.GetArrayLength() > 0)
                    sunriseText = ExtractClockPart(srArr[0].GetString(), "05:50");
                if (daily.TryGetProperty("sunset", out var ssArr) && ssArr.GetArrayLength() > 0)
                    sunsetText = ExtractClockPart(ssArr[0].GetString(), "17:55");
            }

            var hourlySlots = ParseHourlySlots(root, tempC, wmoCode, isDay);
            (string condition, string iconKind, string summary) = InterpretWeather(wmoCode, tempC, feelsC, humidity, isDay);
            string uvLevel = ClassifyUvIndex(uvIndex);

            LatestWeather = new WeatherSnapshot(
                tempC,
                condition,
                city,
                humidity,
                windKmh,
                isDay,
                feelsC,
                summary,
                highC,
                lowC,
                uvIndex,
                uvLevel,
                sunriseText,
                sunsetText,
                iconKind,
                hourlySlots,
                lat,
                lon);

            WeatherUpdated?.Invoke(LatestWeather);
        }
        catch
        {
            WeatherUpdated?.Invoke(LatestWeather);
        }
    }

    public string GetWeatherWebsiteUrl()
    {
        string latStr = LatestWeather.Latitude.ToString("0.0000", CultureInfo.InvariantCulture);
        string lonStr = LatestWeather.Longitude.ToString("0.0000", CultureInfo.InvariantCulture);
        return $"https://weather.com/id-ID/weather/today/l/{latStr},{lonStr}";
    }

    private static List<HourlyWeatherSlot> ParseHourlySlots(JsonElement root, int currentTemp, int currentCode, bool currentIsDay)
    {
        var slots = new List<HourlyWeatherSlot>();
        try
        {
            if (root.TryGetProperty("hourly", out var hourly))
            {
                var times = hourly.GetProperty("time");
                var temps = hourly.GetProperty("temperature_2m");
                var codes = hourly.GetProperty("weather_code");
                bool hasProb = hourly.TryGetProperty("precipitation_probability", out var probs);

                int nowHour = DateTime.Now.Hour;
                int startIndex = 0;
                int len = times.GetArrayLength();

                for (int i = 0; i < len; i++)
                {
                    string? tStr = times[i].GetString();
                    if (tStr != null && DateTime.TryParse(tStr, out DateTime dt) &&
                        dt.Date == DateTime.Now.Date && dt.Hour >= nowHour)
                    {
                        startIndex = i;
                        break;
                    }
                }

                for (int offset = 0; offset < 6 && (startIndex + offset) < len; offset++)
                {
                    int idx = startIndex + offset;
                    string? tStr = times[idx].GetString();
                    int hour = DateTime.Now.AddHours(offset).Hour;
                    if (tStr != null && DateTime.TryParse(tStr, out DateTime dt))
                        hour = dt.Hour;

                    int t = (int)Math.Round(temps[idx].GetDouble());
                    int c = codes[idx].GetInt32();
                    int rainProb = hasProb && idx < probs.GetArrayLength() && probs[idx].ValueKind == JsonValueKind.Number
                        ? probs[idx].GetInt32()
                        : 0;

                    bool slotDay = hour is >= 6 and < 18;
                    (_, string iconKind, _) = InterpretWeather(c, t, t, 65, slotDay);

                    slots.Add(new HourlyWeatherSlot(
                        offset == 0 ? "Now" : $"{hour:00}",
                        iconKind,
                        t,
                        rainProb));
                }
            }
        }
        catch { }

        if (slots.Count == 0)
        {
            int h = DateTime.Now.Hour;
            (_, string baseKind, _) = InterpretWeather(currentCode, currentTemp, currentTemp, 65, currentIsDay);
            for (int i = 0; i < 6; i++)
            {
                int slotHour = (h + i) % 24;
                slots.Add(new HourlyWeatherSlot(i == 0 ? "Now" : $"{slotHour:00}", baseKind, currentTemp - (i % 2)));
            }
        }

        return slots;
    }

    private static string ExtractClockPart(string? isoDateTime, string fallback)
    {
        if (string.IsNullOrWhiteSpace(isoDateTime)) return fallback;
        if (DateTime.TryParse(isoDateTime, out DateTime dt))
            return dt.ToString("HH:mm");
        int idx = isoDateTime.IndexOf('T');
        return idx >= 0 && idx + 5 < isoDateTime.Length ? isoDateTime[(idx + 1)..(idx + 6)] : fallback;
    }

    private static string ClassifyUvIndex(int uv) => uv switch
    {
        <= 2 => "Low",
        <= 5 => "Moderate",
        <= 7 => "High",
        <= 10 => "Very High",
        _ => "Extreme"
    };

    private static async Task<(double Lat, double Lon, string City)> ResolveLaptopLocationAsync()
    {
        double? gpsLat = null;
        double? gpsLon = null;

        try
        {
            var geolocator = new Geolocator { DesiredAccuracyInMeters = 1000 };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            Geoposition pos = await geolocator.GetGeopositionAsync().AsTask(cts.Token);
            gpsLat = pos.Coordinate.Point.Position.Latitude;
            gpsLon = pos.Coordinate.Point.Position.Longitude;
        }
        catch { }

        try
        {
            string ipJson = await Http.GetStringAsync("https://ipwho.is/");
            using JsonDocument doc = JsonDocument.Parse(ipJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.GetBoolean())
            {
                string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                string region = root.TryGetProperty("region", out var r) ? r.GetString() ?? "" : "";
                double lat = gpsLat ?? (root.TryGetProperty("latitude", out var la) ? la.GetDouble() : -6.2088);
                double lon = gpsLon ?? (root.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 106.8456);

                string label = !string.IsNullOrWhiteSpace(city)
                    ? city
                    : !string.IsNullOrWhiteSpace(region) ? region : "Lokasi Laptop";
                return (lat, lon, label);
            }
        }
        catch { }

        if (gpsLat.HasValue && gpsLon.HasValue)
            return (gpsLat.Value, gpsLon.Value, "Lokasi GPS Laptop");

        return (-6.2088, 106.8456, RegionInfo.CurrentRegion.DisplayName);
    }

    private static (string Condition, string IconKind, string NaturalSummary) InterpretWeather(
        int wmoCode, int tempC, int feelsC, int humidity, bool isDay)
    {
        return wmoCode switch
        {
            0 => isDay
                ? ("Mostly Sunny", "Sun",
                   tempC >= 32
                       ? $"Cerah dan hangat siang ini (terasa {feelsC}°C) — tetap terhidrasi saat beraktivitas ya."
                       : "Langit bersih dan cerah sepanjang beberapa jam ke depan, udara segar untuk fokus kerja.")
                : ("Clear Night", "Night",
                   "Langit malam bersih dan tenang, udara sejuk menemani waktu istirahat atau santai."),

            1 or 2 => isDay
                ? ("Partly Cloudy", "PartlyCloudy",
                   tempC >= 32
                       ? $"Berawan sebagian dengan suhu terasa {feelsC}°C — nyaman untuk produktif di dalam ruangan."
                       : "Awan tipis menaungi langit siang ini, sejuk dan nyaman tanpa silau berlebih.")
                : ("Partly Cloudy", "Night",
                   "Malam berawan tipis dan teduh, suasana tenang untuk menikmati musik atau bersantai."),

            3 => ("Mostly Cloudy", "Cloudy",
                  "Langit didominasi awan teduh beberapa jam ke depan, udara terasa lebih adem."),

            45 or 48 => ("Foggy", "Cloudy",
                         "Udara berkabut lembut dengan kelembapan tinggi di sekitar lokasimu."),

            51 or 53 or 55 or 61 or 80 => ("Light Rain", "Rain",
                                           "Gerimis ringan turun di sekitar lokasimu — waktu yang pas sambil seduh minuman hangat."),

            63 or 65 or 81 or 82 => ("Rain Showers", "Rain",
                                     "Hujan turun cukup merata di area ini, siapkan payung bila harus bepergian keluar."),

            95 or 96 or 99 => ("Thunderstorm", "Rain",
                               "Cuaca hujan disertai petir di sekitar lokasimu, tetap aman dan nyaman di dalam ruangan."),

            _ => (isDay ? "Partly Cloudy" : "Clear Night",
                  isDay ? "PartlyCloudy" : "Night",
                  $"Suhu saat ini {tempC}°C (terasa {feelsC}°C) dengan kelembapan {humidity}% — cuaca stabil beberapa jam ke depan.")
        };
    }

    // ================================================================
    //  WIN32 TELEMETRI (BATERAI & JARINGAN)
    // ================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    private void PublishSystemStats()
    {
        int batteryPercent = 100;
        bool isCharging = true;
        if (GetSystemPowerStatus(out var power))
        {
            batteryPercent = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : 100;
            isCharging = power.ACLineStatus == 1;
        }

        (string downText, string upText) = SampleNetworkSpeed();

        SystemStatsUpdated?.Invoke(new SystemStatsSnapshot(
            0,
            batteryPercent,
            isCharging,
            0,
            downText,
            upText,
            "System Audio",
            0,
            0));
    }

    private (string DownText, string UpText) SampleNetworkSpeed()
    {
        try
        {
            long totalRx = 0;
            long totalTx = 0;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var stats = ni.GetIPv4Statistics();
                totalRx += stats.BytesReceived;
                totalTx += stats.BytesSent;
            }

            DateTime now = DateTime.UtcNow;
            if (_prevNetSampleTime == DateTime.MinValue)
            {
                _prevBytesReceived = totalRx;
                _prevBytesSent = totalTx;
                _prevNetSampleTime = now;
                return ("0.0 KB/s", "0.0 KB/s");
            }

            double elapsedSec = Math.Max(0.2, (now - _prevNetSampleTime).TotalSeconds);
            double rxPerSec = Math.Max(0, (totalRx - _prevBytesReceived) / elapsedSec);
            double txPerSec = Math.Max(0, (totalRx >= _prevBytesReceived ? (totalTx - _prevBytesSent) : 0) / elapsedSec);

            _prevBytesReceived = totalRx;
            _prevBytesSent = totalTx;
            _prevNetSampleTime = now;

            return (FormatRate(rxPerSec), FormatRate(txPerSec));
        }
        catch
        {
            return ("0.0 KB/s", "0.0 KB/s");
        }
    }

    private static string FormatRate(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb >= 1024.0)
            return $"{kb / 1024.0:0.1} MB/s";
        return $"{kb:0.1} KB/s";
    }
}
