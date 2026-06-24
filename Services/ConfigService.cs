using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ADC_Rec.Models.Filters;

namespace ADC_Rec.Services
{
    public class ConfigService
    {
        public class FilterConfig
        {
            public FilterType Type { get; set; }
            public string Name { get; set; } = "";
            public ChannelBinding Channels { get; set; }
            public bool IsEnabled { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        }

        public class AppConfig
        {
            public List<FilterConfig> Filters { get; set; } = new List<FilterConfig>();
            public float[] Gains { get; set; } = new float[4];
            public float[] Pans { get; set; } = new float[4];
        }

        public static void SaveConfig(string filePath, AudioMixService audioService)
        {
            var config = new AppConfig();
            var gains = audioService.GetChannelGainsSnapshot();
            Array.Copy(gains, config.Gains, 4);

            var pans = audioService.GetChannelPansSnapshot();
            Array.Copy(pans, config.Pans, 4);
            
            foreach (var filter in audioService.Filters)
            {
                var fConfig = new FilterConfig
                {
                    Type = filter.Type,
                    Name = filter.Name,
                    Channels = filter.Channels,
                    IsEnabled = filter.IsEnabled
                };

                // Use reflection or explicit check to get parameters
                // This is a simplified approach
                foreach (var prop in filter.GetType().GetProperties())
                {
                    if (prop.CanRead && prop.Name != "Id" && prop.Name != "Type" && prop.Name != "Name" && prop.Name != "Channels" && prop.Name != "IsEnabled" && prop.Name != "PropertyChanged")
                    {
                        var val = prop.GetValue(filter);
                        if (val != null)
                            fConfig.Parameters[prop.Name] = val;
                    }
                }
                config.Filters.Add(fConfig);
            }

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        public static void LoadConfig(string filePath, AudioMixService audioService)
        {
            if (!File.Exists(filePath)) return;
            string json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            if (config == null) return;

            for (int i = 0; i < 4; i++)
            {
                audioService.SetChannelGain(i, config.Gains[i]);
                audioService.SetChannelPan(i, config.Pans[i]);
            }

            audioService.Filters.Clear();
            foreach (var fConfig in config.Filters)
            {
                IFilter filter = fConfig.Type switch
                {
                    FilterType.PeakingEQ => new PeakingEQFilter(),
                    FilterType.LowShelf => new ShelfFilter(true),
                    FilterType.HighShelf => new ShelfFilter(false),
                    FilterType.Compressor => new CompressorFilter(),
                    FilterType.NoiseSuppression => new NoiseSuppressionFilter(),
                    FilterType.Reverb => new ReverbFilter(),
                    FilterType.Delay => new DelayFilter(),
                    _ => throw new NotSupportedException()
                };

                filter.Name = fConfig.Name;
                filter.Channels = fConfig.Channels;
                filter.IsEnabled = fConfig.IsEnabled;

                foreach (var param in fConfig.Parameters)
                {
                    var prop = filter.GetType().GetProperty(param.Key);
                    if (prop != null && prop.CanWrite)
                    {
                        var value = JsonSerializer.Deserialize(param.Value.ToString()!, prop.PropertyType);
                        prop.SetValue(filter, value);
                    }
                }
                audioService.Filters.Add(filter);
            }
        }
    }
}
