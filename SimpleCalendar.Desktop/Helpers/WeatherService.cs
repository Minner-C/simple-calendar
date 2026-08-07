using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers
{
    public class WeatherInfo
    {
        public string City { get; set; } = "";
        public string TempC { get; set; } = "";
        public string FeelsLikeC { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "🌤️";
        public string WeatherCode { get; set; } = "116";
        public string Humidity { get; set; } = "";
        public string WindKmph { get; set; } = "";
        public List<WeatherForecast> Forecast { get; set; } = new();
    }

    public class WeatherForecast
    {
        public string Date { get; set; } = "";
        public string MaxTempC { get; set; } = "";
        public string MinTempC { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "🌤️";
        public string WeatherCode { get; set; } = "116";
        public List<HourlyForecast> Hourly { get; set; } = new();
    }

    public class HourlyForecast
    {
        public string Time { get; set; } = "";
        public string TempC { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "🌤️";
        public string WeatherCode { get; set; } = "116";
        public string FeelsLikeC { get; set; } = "";
        public string Humidity { get; set; } = "";
        public string ChanceOfRain { get; set; } = "";
    }

    public static class WeatherService
    {
        private static readonly HttpClientHandler _handler = new()
        {
            CheckCertificateRevocationList = false
        };
        private static readonly HttpClient _httpClient = new(_handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private static WeatherInfo? _cachedWeather;
        private static DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
        private static string? _cachedProvider;

        private static readonly Dictionary<string, (double lat, double lon)> CityCoords = new()
        {
            ["北京"] = (39.904, 116.407), ["上海"] = (31.230, 121.474),
            ["广州"] = (23.129, 113.264), ["深圳"] = (22.543, 114.058),
            ["成都"] = (30.573, 104.067), ["杭州"] = (30.274, 120.155),
            ["武汉"] = (30.593, 114.305), ["南京"] = (32.060, 118.797),
            ["重庆"] = (29.563, 106.551), ["西安"] = (34.341, 108.939),
            ["长沙"] = (28.228, 112.939), ["苏州"] = (31.299, 120.585),
            ["天津"] = (39.084, 117.201), ["郑州"] = (34.747, 113.625),
            ["青岛"] = (36.067, 120.383), ["大连"] = (38.914, 121.614),
            ["厦门"] = (24.480, 118.089), ["合肥"] = (31.821, 117.227),
            ["昆明"] = (25.039, 102.718), ["沈阳"] = (41.806, 123.432),
            ["哈尔滨"] = (45.803, 126.535), ["济南"] = (36.651, 116.997),
            ["福州"] = (26.075, 119.297), ["太原"] = (37.871, 112.549),
            ["贵阳"] = (26.647, 106.630), ["南宁"] = (22.817, 108.367),
            ["兰州"] = (36.061, 103.834), ["海口"] = (20.017, 110.349),
            ["乌鲁木齐"] = (43.826, 87.617), ["拉萨"] = (29.660, 91.132),
            ["长春"] = (43.817, 125.323), ["石家庄"] = (38.043, 114.515),
            ["呼和浩特"] = (40.842, 111.749), ["银川"] = (38.487, 106.232),
            ["西宁"] = (36.617, 101.778), ["南昌"] = (28.682, 115.858),
            ["珠海"] = (22.271, 113.577), ["佛山"] = (23.022, 113.122),
            ["东莞"] = (23.043, 113.763), ["中山"] = (22.517, 113.393),
            ["无锡"] = (31.491, 120.312), ["常州"] = (31.811, 119.974),
            ["宁波"] = (29.868, 121.550), ["温州"] = (27.994, 120.699),
            ["烟台"] = (37.464, 121.448), ["潍坊"] = (36.707, 119.162),
            ["威海"] = (37.513, 122.120), ["泰安"] = (36.195, 117.088),
            ["临沂"] = (35.104, 118.356), ["淄博"] = (36.813, 118.054),
            ["济宁"] = (35.415, 116.587), ["枣庄"] = (34.856, 117.557),
            ["德州"] = (37.434, 116.357), ["聊城"] = (36.457, 115.985),
            ["滨州"] = (37.382, 117.971), ["菏泽"] = (35.233, 115.481),
            ["日照"] = (35.416, 119.527), ["东营"] = (37.435, 118.674),
        };

        public static async Task<WeatherInfo?> GetWeatherAsync(string city, string gaodeKey = "")
        {
            var settings = ClockSettingsManager.LoadSettings();
            return await GetWeatherAsync(city, gaodeKey, settings.WeatherProvider ?? "auto", settings.ApiHzId ?? "", settings.ApiHzKey ?? "");
        }

        /// <summary>
        /// 获取天气（支持指定接口）
        /// </summary>
        /// <param name="provider">auto / openmeteo / wttr / gaode / apihz</param>
        public static async Task<WeatherInfo?> GetWeatherAsync(string city, string gaodeKey, string provider, string apihzId = "", string apihzKey = "")
        {
            if (_cachedWeather != null && DateTime.Now - _lastFetch < CacheDuration && 
                _cachedProvider == provider)
            {
                return _cachedWeather;
            }

            WeatherInfo? weather = null;

            // 根据用户选择的接口获取
            switch (provider?.ToLowerInvariant())
            {
                case "openmeteo":
                    weather = await TryOpenMeteoAsync(city);
                    break;
                case "wttr":
                    weather = await TryWttrInAsync(city);
                    break;
                case "gaode":
                    if (!string.IsNullOrEmpty(gaodeKey))
                        weather = await TryGaodeAsync(city, gaodeKey);
                    else
                        System.Diagnostics.Debug.WriteLine("[Weather] 高德接口需要Key");
                    break;
                case "apihz":
                    weather = await TryApiHzAsync(city, apihzId, apihzKey);
                    break;
                default: // auto
                    weather = await TryOpenMeteoAsync(city);
                    if (weather == null) weather = await TryWttrInAsync(city);
                    if (weather == null) weather = await TryApiHzAsync(city, apihzId, apihzKey);
                    if (weather == null && !string.IsNullOrEmpty(gaodeKey))
                        weather = await TryGaodeAsync(city, gaodeKey);
                    break;
            }

            if (weather != null)
            {
                _cachedWeather = weather;
                _lastFetch = DateTime.Now;
                _cachedProvider = provider;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Weather] 接口({provider})获取失败");
            }

            return weather;
        }

        /// <summary>
        /// 测试接口联通性
        /// </summary>
        /// <param name="provider">openmeteo / wttr / gaode / apihz</param>
        /// <param name="city">测试城市</param>
        /// <param name="gaodeKey">高德Key（仅gaode接口需要）</param>
        /// <param name="apihzId">消息盒子开发者ID</param>
        /// <param name="apihzKey">消息盒子开发者KEY</param>
        /// <returns>(是否成功, 描述信息)</returns>
        public static async Task<(bool success, string message)> TestProviderAsync(string provider, string city, string gaodeKey = "", string apihzId = "", string apihzKey = "")
        {
            try
            {
                WeatherInfo? weather = null;
                string providerName = "";

                switch (provider?.ToLowerInvariant())
                {
                    case "openmeteo":
                        providerName = "Open-Meteo";
                        weather = await TryOpenMeteoAsync(city);
                        break;
                    case "wttr":
                        providerName = "wttr.in";
                        weather = await TryWttrInAsync(city);
                        break;
                    case "gaode":
                        providerName = "高德天气";
                        if (string.IsNullOrEmpty(gaodeKey))
                            return (false, "高德接口需要Key，请先填写");
                        weather = await TryGaodeAsync(city, gaodeKey);
                        break;
                    case "apihz":
                        providerName = "消息盒子(中国气象局)";
                        if (string.IsNullOrEmpty(apihzId) || string.IsNullOrEmpty(apihzKey))
                            return (false, "消息盒子接口需要开发者ID和KEY，请先到apihz.cn注册获取");
                        weather = await TryApiHzAsync(city, apihzId, apihzKey);
                        break;
                    default:
                        return (false, "未知接口");
                }

                if (weather != null)
                {
                    string forecastInfo = weather.Forecast.Count > 0 
                        ? $", 预报{weather.Forecast.Count}天" 
                        : ", 无预报数据";
                    return (true, $"{providerName} 连通成功: {weather.City} {weather.TempC}°C {weather.Description}{forecastInfo}");
                }
                else
                {
                    return (false, $"{providerName} 接口失败，请检查网络或参数");
                }
            }
            catch (Exception ex)
            {
                return (false, $"测试异常: {ex.Message}");
            }
        }

        private static async Task<WeatherInfo?> TryOpenMeteoAsync(string city)
        {
            try
            {
                double lat, lon;
                if (CityCoords.TryGetValue(city, out var coord))
                {
                    lat = coord.lat;
                    lon = coord.lon;
                }
                else
                {
                    var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&language=zh&count=1";
                    var geoJson = await _httpClient.GetStringAsync(geoUrl);
                    var geoDoc = JsonDocument.Parse(geoJson);
                    var geoRoot = geoDoc.RootElement;

                    if (!geoRoot.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Weather] Open-Meteo 地理编码失败: 未找到 {city}");
                        return null;
                    }

                    lat = results[0].GetProperty("latitude").GetDouble();
                    lon = results[0].GetProperty("longitude").GetDouble();
                }

                var forecastUrl = $"https://api.open-meteo.com/v1/forecast?" +
                    $"latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                    $"&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                    $"&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                    $"&hourly=temperature_2m,weather_code,apparent_temperature,relative_humidity_2m,precipitation_probability" +
                    $"&timezone=Asia%2FShanghai&forecast_days=3";

                var json = await _httpClient.GetStringAsync(forecastUrl);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var weather = new WeatherInfo { City = city };

                if (root.TryGetProperty("current", out var current))
                {
                    weather.TempC = FormatNum(current, "temperature_2m");
                    weather.FeelsLikeC = FormatNum(current, "apparent_temperature");
                    weather.Humidity = FormatNum(current, "relative_humidity_2m");
                    weather.WindKmph = FormatNum(current, "wind_speed_10m");

                    var wmoCode = FormatInt(current, "weather_code");
                    var mappedCode = WmoToWttrCode(wmoCode).ToString();
                    weather.WeatherCode = mappedCode;
                    weather.Description = GetWeatherDescZh(mappedCode);
                    weather.Icon = GetWeatherIcon(mappedCode);
                }

                if (root.TryGetProperty("daily", out var daily) &&
                    root.TryGetProperty("hourly", out var hourly))
                {
                    var dailyTimes = GetStringArray(daily, "time");
                    var dailyMaxTemps = GetDoubleArray(daily, "temperature_2m_max");
                    var dailyMinTemps = GetDoubleArray(daily, "temperature_2m_min");
                    var dailyWmoCodes = GetIntArray(daily, "weather_code");

                    var hourlyTimes = GetStringArray(hourly, "time");
                    var hourlyTemps = GetDoubleArray(hourly, "temperature_2m");
                    var hourlyWmoCodes = GetIntArray(hourly, "weather_code");
                    var hourlyApparent = GetDoubleArray(hourly, "apparent_temperature");
                    var hourlyHumidity = GetDoubleArray(hourly, "relative_humidity_2m");
                    var hourlyRain = GetDoubleArray(hourly, "precipitation_probability");

                    for (int d = 0; d < dailyTimes.Count; d++)
                    {
                        var forecast = new WeatherForecast
                        {
                            Date = dailyTimes[d],
                            MaxTempC = d < dailyMaxTemps.Count ? dailyMaxTemps[d].ToString("0") : "",
                            MinTempC = d < dailyMinTemps.Count ? dailyMinTemps[d].ToString("0") : "",
                        };

                        if (d < dailyWmoCodes.Count)
                        {
                            var fCode = WmoToWttrCode(dailyWmoCodes[d]).ToString();
                            forecast.WeatherCode = fCode;
                            forecast.Description = GetWeatherDescZh(fCode);
                            forecast.Icon = GetWeatherIcon(fCode);
                        }

                        string datePrefix = dailyTimes[d] + "T";
                        for (int h = 0; h < hourlyTimes.Count; h++)
                        {
                            if (!hourlyTimes[h].StartsWith(datePrefix)) continue;

                            string timeStr = hourlyTimes[h].Substring(11, 5);
                            var hForecast = new HourlyForecast
                            {
                                Time = timeStr,
                                TempC = h < hourlyTemps.Count ? hourlyTemps[h].ToString("0") : "",
                                FeelsLikeC = h < hourlyApparent.Count ? hourlyApparent[h].ToString("0") : "",
                                Humidity = h < hourlyHumidity.Count ? hourlyHumidity[h].ToString("0") : "",
                                ChanceOfRain = h < hourlyRain.Count ? hourlyRain[h].ToString("0") : "",
                            };

                            if (h < hourlyWmoCodes.Count)
                            {
                                var hCode = WmoToWttrCode(hourlyWmoCodes[h]).ToString();
                                hForecast.WeatherCode = hCode;
                                hForecast.Description = GetWeatherDescZh(hCode);
                                hForecast.Icon = GetWeatherIcon(hCode);
                            }

                            forecast.Hourly.Add(hForecast);
                        }

                        weather.Forecast.Add(forecast);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Weather] Open-Meteo 成功: {city} {weather.TempC}°C {weather.Description}");
                return weather;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Weather] Open-Meteo 失败: {ex.Message}");
                return null;
            }
        }

        private static async Task<WeatherInfo?> TryWttrInAsync(string city)
        {
            try
            {
                var url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1&lang=zh";
                var response = await _httpClient.GetStringAsync(url);
                var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var weather = new WeatherInfo { City = city };

                if (root.TryGetProperty("current_condition", out var conditions) && conditions.GetArrayLength() > 0)
                {
                    var current = conditions[0];
                    weather.TempC = current.GetProperty("temp_C").GetString() ?? "";
                    weather.FeelsLikeC = current.GetProperty("FeelsLikeC").GetString() ?? "";
                    weather.Humidity = current.GetProperty("humidity").GetString() ?? "";
                    weather.WindKmph = current.GetProperty("windspeedKmph").GetString() ?? "";

                    var currentCode = current.GetProperty("weatherCode").GetString() ?? "";
                    weather.Description = ParseDescription(current, currentCode);
                    weather.Icon = GetWeatherIcon(currentCode);
                    weather.WeatherCode = currentCode;
                }

                if (root.TryGetProperty("weather", out var weatherDays))
                {
                    foreach (var day in weatherDays.EnumerateArray())
                    {
                        var forecast = new WeatherForecast
                        {
                            Date = day.GetProperty("date").GetString() ?? "",
                            MaxTempC = day.GetProperty("maxtempC").GetString() ?? "",
                            MinTempC = day.GetProperty("mintempC").GetString() ?? "",
                        };

                        if (day.TryGetProperty("hourly", out var hourly))
                        {
                            foreach (var hour in hourly.EnumerateArray())
                            {
                                var time = hour.GetProperty("time").GetString() ?? "";
                                if (int.TryParse(time, out int timeInt))
                                {
                                    int hourVal = timeInt / 100;
                                    var hForecast = new HourlyForecast
                                    {
                                        Time = $"{hourVal:D2}:00",
                                        TempC = hour.GetProperty("tempC").GetString() ?? "",
                                        FeelsLikeC = hour.TryGetProperty("FeelsLikeC", out var fl) ? fl.GetString() ?? "" : "",
                                        Humidity = hour.TryGetProperty("humidity", out var hm) ? hm.GetString() ?? "" : "",
                                        ChanceOfRain = hour.TryGetProperty("chanceofrain", out var cr) ? cr.GetString() ?? "" : "",
                                    };
                                    var hCode = hour.GetProperty("weatherCode").GetString() ?? "";
                                    hForecast.Description = ParseDescription(hour, hCode);
                                    hForecast.Icon = GetWeatherIcon(hCode);
                                    hForecast.WeatherCode = hCode;
                                    forecast.Hourly.Add(hForecast);
                                }

                                if (time == "1200")
                                {
                                    var code12 = hour.GetProperty("weatherCode").GetString() ?? "";
                                    forecast.Description = ParseDescription(hour, code12);
                                    forecast.Icon = GetWeatherIcon(code12);
                                    forecast.WeatherCode = code12;
                                }
                            }
                        }

                        weather.Forecast.Add(forecast);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Weather] wttr.in 成功: {city} {weather.TempC}°C {weather.Description}");
                return weather;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Weather] wttr.in 失败: {ex.Message}");
                return null;
            }
        }

        private static async Task<WeatherInfo?> TryGaodeAsync(string city, string apiKey)
        {
            try
            {
                // 1. 地理编码：城市名 -> adcode和坐标
                var geoUrl = $"https://restapi.amap.com/v3/geocode/geo?address={Uri.EscapeDataString(city)}&output=JSON&key={apiKey}";
                var geoJson = await _httpClient.GetStringAsync(geoUrl);
                var geoDoc = JsonDocument.Parse(geoJson);
                var geoRoot = geoDoc.RootElement;

                if (!geoRoot.TryGetProperty("geocodes", out var geocodes) || geocodes.GetArrayLength() == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Weather] 高德地理编码失败: 未找到 {city}");
                    return null;
                }

                var adcode = geocodes[0].GetProperty("adcode").GetString();
                var location = geocodes[0].GetProperty("location").GetString();
                if (string.IsNullOrEmpty(adcode) || string.IsNullOrEmpty(location))
                {
                    System.Diagnostics.Debug.WriteLine($"[Weather] 高德地理编码失败: 无adcode或坐标");
                    return null;
                }

                // 2. 使用adcode查询天气（高德天气API必须用adcode）
                var weatherUrl = $"https://restapi.amap.com/v3/weather/weatherInfo?city={adcode}&output=JSON&key={apiKey}";
                var weatherJson = await _httpClient.GetStringAsync(weatherUrl);
                var weatherDoc = JsonDocument.Parse(weatherJson);
                var weatherRoot = weatherDoc.RootElement;

                if (!weatherRoot.TryGetProperty("lives", out var lives) || lives.GetArrayLength() == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Weather] 高德天气获取失败: adcode={adcode}");
                    return null;
                }

                var live = lives[0];
                var weather = new WeatherInfo { City = city };
                weather.TempC = live.GetProperty("temperature").GetString() ?? "";
                weather.Humidity = live.GetProperty("humidity").GetString() ?? "";
                weather.WindKmph = live.GetProperty("windpower").GetString() ?? "";

                var weatherDesc = live.GetProperty("weather").GetString() ?? "";
                weather.Description = weatherDesc;
                weather.WeatherCode = GetGaodeWeatherCode(weatherDesc);
                weather.Icon = GetWeatherIcon(weather.WeatherCode);

                System.Diagnostics.Debug.WriteLine($"[Weather] 高德天气成功: {city}({adcode}) {weather.TempC}°C {weather.Description}");
                return weather;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Weather] 高德天气失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 消息盒子天气API（中国气象局数据源）- IP自动定位，无需指定城市
        /// 文档: http://apihz.cn/api/tqtqybip.html
        /// </summary>
        private static async Task<WeatherInfo?> TryApiHzAsync(string city, string apiId = "", string apiKey = "")
        {
            try
            {
                // 如果用户未配置自己的KEY，使用公共测试KEY（可能被限流）
                string id = string.IsNullOrEmpty(apiId) ? "88888888" : apiId;
                string key = string.IsNullOrEmpty(apiKey) ? "88888888" : apiKey;
                var url = $"https://cn.apihz.cn/api/tianqi/tqybip.php?id={id}&key={key}&day=3&hourtype=1";

                var json = await _httpClient.GetStringAsync(url);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200)
                {
                    var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[Weather] 消息盒子接口失败: {msg}");
                    return null;
                }

                string shi = root.TryGetProperty("shi", out var shiEl) ? shiEl.GetString() ?? "" : "";
                string sheng = root.TryGetProperty("sheng", out var shengEl) ? shengEl.GetString() ?? "" : "";
                string locationName = !string.IsNullOrEmpty(shi) ? shi : (!string.IsNullOrEmpty(sheng) ? sheng : city);

                var weather = new WeatherInfo { City = locationName };

                if (root.TryGetProperty("nowinfo", out var nowinfo))
                {
                    weather.TempC = GetJsonString(nowinfo, "temperature");
                    weather.FeelsLikeC = GetJsonString(nowinfo, "feelst");
                    weather.Humidity = GetJsonString(nowinfo, "humidity");
                    var windDir = GetJsonString(nowinfo, "windDirection");
                    var windScale = GetJsonString(nowinfo, "windScale");
                    weather.WindKmph = string.IsNullOrEmpty(windDir) ? windScale : $"{windDir} {windScale}";
                }

                string weather1 = root.TryGetProperty("weather1", out var w1) ? w1.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(weather1) && root.TryGetProperty("weather2", out var w2))
                    weather1 = w2.GetString() ?? "";
                weather.Description = weather1;
                weather.WeatherCode = MapApiHzDescToCode(weather1);
                weather.Icon = GetWeatherIcon(weather.WeatherCode);

                for (int d = 1; d <= 3; d++)
                {
                    string dayKey = d == 1 ? "" : $"weatherday{d}";
                    JsonElement dayEl;
                    if (d == 1)
                        dayEl = root;
                    else if (!root.TryGetProperty(dayKey, out dayEl))
                        continue;

                    var forecast = new WeatherForecast
                    {
                        Date = DateTime.Now.AddDays(d - 1).ToString("yyyy-MM-dd"),
                    };

                    string wd1 = GetJsonString(dayEl, "wd1");
                    string wd2 = GetJsonString(dayEl, "wd2");
                    forecast.MaxTempC = wd1;
                    forecast.MinTempC = wd2;

                    string dayWeather = GetJsonString(dayEl, "weather1");
                    if (string.IsNullOrEmpty(dayWeather)) dayWeather = GetJsonString(dayEl, "weather2");
                    if (string.IsNullOrEmpty(dayWeather)) dayWeather = weather1;
                    forecast.Description = dayWeather;
                    forecast.WeatherCode = MapApiHzDescToCode(dayWeather);
                    forecast.Icon = GetWeatherIcon(forecast.WeatherCode);

                    if (d == 1 && root.TryGetProperty("hour1", out var hour1))
                    {
                        foreach (var hItem in hour1.EnumerateArray())
                        {
                            var hTime = GetJsonString(hItem, "时间");
                            var hTemp = GetJsonString(hItem, "气温");
                            var hDesc = GetJsonString(hItem, "天气");
                            var hHumidity = GetJsonString(hItem, "湿度");
                            if (hTemp.EndsWith("℃")) hTemp = hTemp.TrimEnd('℃');

                            var hCode = MapApiHzDescToCode(hDesc);
                            forecast.Hourly.Add(new HourlyForecast
                            {
                                Time = hTime,
                                TempC = hTemp,
                                Description = hDesc,
                                Humidity = hHumidity?.TrimEnd('%') ?? "",
                                Icon = GetWeatherIcon(hCode),
                                WeatherCode = hCode
                            });
                        }
                    }

                    weather.Forecast.Add(forecast);
                }

                System.Diagnostics.Debug.WriteLine($"[Weather] 消息盒子接口成功: {weather.City} {weather.TempC}°C {weather.Description}");
                return weather;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Weather] 消息盒子接口失败: {ex.Message}");
                return null;
            }
        }

        private static string GetJsonString(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v))
            {
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetDouble().ToString("0.#"),
                    _ => ""
                };
            }
            return "";
        }

        private static string MapApiHzDescToCode(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return "116";
            if (desc.Contains("晴")) return "113";
            if (desc.Contains("多云")) return "116";
            if (desc.Contains("阴")) return "119";
            if (desc.Contains("雷")) return "389";
            if (desc.Contains("暴雨")) return "308";
            if (desc.Contains("大雨")) return "302";
            if (desc.Contains("中雨")) return "299";
            if (desc.Contains("小雨") || desc.Contains("阵雨") || desc.Contains("毛毛雨") || desc.Contains("雨")) return "176";
            if (desc.Contains("暴雪")) return "338";
            if (desc.Contains("大雪")) return "332";
            if (desc.Contains("中雪")) return "329";
            if (desc.Contains("小雪") || desc.Contains("阵雪") || desc.Contains("雪")) return "179";
            if (desc.Contains("雾") || desc.Contains("霾")) return "143";
            if (desc.Contains("冰雹")) return "377";
            return "116";
        }

        private static string GetGaodeWeatherCode(string desc)
        {
            if (desc.Contains("晴")) return "113";
            if (desc.Contains("多云")) return "116";
            if (desc.Contains("阴")) return "119";
            if (desc.Contains("雨")) return "176";
            if (desc.Contains("雪")) return "179";
            if (desc.Contains("雷")) return "200";
            if (desc.Contains("雾")) return "143";
            return "116";
        }

        private static int WmoToWttrCode(int wmoCode)
        {
            return wmoCode switch
            {
                0 => 113,
                1 => 116,
                2 => 116,
                3 => 119,
                45 or 48 => 143,
                51 => 263,
                53 => 266,
                55 => 176,
                56 or 57 => 281,
                61 => 296,
                63 => 299,
                65 => 308,
                66 or 67 => 314,
                71 => 326,
                73 => 329,
                75 => 338,
                77 => 338,
                80 => 353,
                81 => 356,
                82 => 359,
                85 => 371,
                86 => 371,
                95 => 389,
                96 or 99 => 395,
                _ => 116,
            };
        }

        private static string FormatNum(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble().ToString("0");
            return "";
        }

        private static int FormatInt(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
            return 0;
        }

        private static List<string> GetStringArray(JsonElement el, string prop)
        {
            var list = new List<string>();
            if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    list.Add(item.GetString() ?? "");
            return list;
        }

        private static List<double> GetDoubleArray(JsonElement el, string prop)
        {
            var list = new List<double>();
            if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    list.Add(item.ValueKind == JsonValueKind.Number ? item.GetDouble() : 0);
            return list;
        }

        private static List<int> GetIntArray(JsonElement el, string prop)
        {
            var list = new List<int>();
            if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    list.Add(item.ValueKind == JsonValueKind.Number ? item.GetInt32() : 0);
            return list;
        }

        public static void ClearCache()
        {
            _cachedWeather = null;
            _lastFetch = DateTime.MinValue;
        }

        private static string GetWeatherIcon(string weatherCode)
        {
            return weatherCode switch
            {
                "113" => "☀️",
                "116" => "⛅",
                "119" => "☁️",
                "122" => "☁️",
                "143" => "🌫️",
                "176" => "🌧️",
                "179" => "🌨️",
                "182" => "🌨️",
                "185" => "🌨️",
                "200" => "⛈️",
                "227" => "🌨️",
                "230" => "❄️",
                "248" or "260" => "🌫️",
                "263" or "266" => "🌦️",
                "281" or "284" => "🌨️",
                "293" or "296" => "🌧️",
                "299" or "302" => "🌧️",
                "305" or "308" => "🌧️",
                "311" or "314" => "🌨️",
                "317" or "320" => "🌨️",
                "323" or "326" => "🌨️",
                "329" or "332" => "❄️",
                "335" or "338" => "❄️",
                "350" or "353" => "🌦️",
                "356" or "359" => "🌧️",
                "362" or "365" => "🌨️",
                "368" or "371" => "🌨️",
                "374" or "377" => "🌨️",
                "386" or "389" => "⛈️",
                "392" or "395" => "⛈️",
                _ => "🌤️"
            };
        }

        public static (string icon, string foregroundHex) GetThemedWeatherIcon(string weatherCode, bool isDark)
        {
            return weatherCode switch
            {
                "113" => isDark ? ("☀", "#FFD54F") : ("☀", "#F59E0B"),
                "116" => isDark ? ("☁", "#90CAF9") : ("☁", "#5C8DB8"),
                "119" or "122" => isDark ? ("☁", "#78909C") : ("☁", "#78909C"),
                "143" or "248" or "260" => isDark ? ("≡", "#B0BEC5") : ("≡", "#90A4AE"),
                "176" or "263" or "266" or "293" or "296" => isDark ? ("🌧", "#64B5F6") : ("🌧", "#3B82F6"),
                "299" or "302" or "305" or "308" or "356" or "359" => isDark ? ("🌧", "#42A5F5") : ("🌧", "#1D4ED8"),
                "179" or "227" or "230" or "323" or "326" or "329" or "332" or "335" or "338" or "368" or "371" => isDark ? ("❄", "#81D4FA") : ("❄", "#3B82F6"),
                "182" or "185" or "281" or "284" or "311" or "314" or "362" or "365" or "374" or "377" => isDark ? ("🌧", "#4FC3F7") : ("🌧", "#0284C7"),
                "200" or "386" or "389" or "392" or "395" => isDark ? ("⚡", "#CE93D8") : ("⚡", "#7C3AED"),
                "350" or "353" => isDark ? ("🌧", "#64B5F6") : ("🌧", "#2563EB"),
                _ => isDark ? ("☁", "#90CAF9") : ("☁", "#5C8DB8"),
            };
        }

        private static string GetWeatherDescZh(string weatherCode)
        {
            return weatherCode switch
            {
                "113" => "晴",
                "116" => "多云",
                "119" => "阴",
                "122" => "阴天",
                "143" => "雾",
                "176" => "小雨",
                "179" => "小雪",
                "182" => "雨夹雪",
                "185" => "冻雨",
                "200" => "雷阵雨",
                "227" => "暴风雪",
                "230" => "暴雪",
                "248" or "260" => "大雾",
                "263" or "266" => "毛毛雨",
                "281" or "284" => "冻毛毛雨",
                "293" or "296" => "小雨",
                "299" or "302" => "大雨",
                "305" or "308" => "暴雨",
                "311" or "314" => "冻雨",
                "317" or "320" => "中到大雪",
                "323" or "326" => "小雪",
                "329" or "332" => "中到大雪",
                "335" or "338" => "大雪",
                "350" or "353" => "阵雨",
                "356" or "359" => "大阵雨",
                "362" or "365" => "雨夹雪",
                "368" or "371" => "阵雪",
                "374" or "377" => "冰粒",
                "386" or "389" => "雷阵雨",
                "392" or "395" => "雷阵雪",
                _ => "未知"
            };
        }

        private static string ParseDescription(JsonElement element, string weatherCode)
        {
            if (element.TryGetProperty("lang_zh", out var langZh) && langZh.GetArrayLength() > 0)
            {
                var val = langZh[0].GetProperty("value").GetString();
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return GetWeatherDescZh(weatherCode);
        }
    }
}