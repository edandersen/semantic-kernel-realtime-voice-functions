using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using System.Net.Http;
using System.Net.Http.Json;

public class LightsPlugin
{
   public static string HueBridgeIp { get; set; } = "192.168.50.138"; // set to null to auto-discover
   public static string HueUsername {get; set; } = ""; // Replace with your Philips Hue bridge username

   [KernelFunction("get_lights")]
   [Description("Gets a list of lights and their current state")]
   [return: Description("An array of lights")]
   public async Task<List<LightModel>> GetLightsAsync()
    {
        var hueLights = await HueApiGet<Dictionary<string, HueLightModel>>("lights");
        var lights = new List<LightModel>();
        if (hueLights != null)
        {
            foreach (var hueLight in hueLights)
            {
                lights.Add(new LightModel
                {
                    Id = int.Parse(hueLight.Key),
                    Name = hueLight.Value.Name,
                    IsOn = hueLight.Value.State.On
                });
            }
        }
        return lights;
    }

    private static async Task<T> HueApiGet<T>(string apiPath)
    {
        string baseUrl = await GetBaseUrl();

        var callerHttpClient = new HttpClient();
        var response = await callerHttpClient.GetAsync($"{baseUrl}{apiPath}");
        try {
        response.EnsureSuccessStatusCode();
        } catch (Exception e) {
            Console.WriteLine(e.Message);
            throw;
        }
        var model = await response.Content.ReadFromJsonAsync<T>();

        return model;
    }

    private static async Task<bool> HueApiPut(string apiPath, object content)
    {
        string baseUrl = await GetBaseUrl();

        var callerHttpClient = new HttpClient();
        var response = await callerHttpClient.PutAsJsonAsync($"{baseUrl}{apiPath}", content);

        return response.IsSuccessStatusCode;
    }

    private static async Task<string> GetBaseUrl()
    {
        if (HueBridgeIp == null)
        {
            var httpClient = new HttpClient();
            var discoveryResponse = await httpClient.GetAsync("https://discovery.meethue.com/");
            discoveryResponse.EnsureSuccessStatusCode();

            var bridges = await discoveryResponse.Content.ReadFromJsonAsync<List<HueBridge>>();

            if (bridges == null || bridges.Count == 0)
            {
                  throw new Exception("No Philips Hue bridges found.");
            }

            HueBridgeIp = bridges.First().InternalIpAddress;
        }
        
        var baseUrl = $"http://{HueBridgeIp}/api/{HueUsername}/";
        return baseUrl;
    }

   [KernelFunction("change_state")]
   [Description("Changes the state of the light. Set id to null to change all lights")]
   [return: Description("Returns true if the status change was a success")]
   public async Task<bool> ChangeStateAsync(int? id, bool isOn)
   {
      if (id == null)
      {
         var lights = await GetLightsAsync();
         foreach (var light in lights)
         {
            await ChangeStateAsync(light.Id, isOn);
         }
         return true;
      }

      var response = await HueApiPut($"lights/{id}/state", new { on = isOn });

      return response;
   }

   [KernelFunction("change_light_color")]
   [Description("Changes the color of the light to an RGB value. Set id to null to change all lights")]
   [return: Description("Returns true if the color change was a success")]
   public async Task<bool> ChangeStateAsync(int? id, int red, int green, int blue)
   {
      if (id == null)
      {
         var lights = await GetLightsAsync();
         foreach (var light in lights)
         {
            await ChangeStateAsync(light.Id, red, green, blue);
         }
         return true;
      }

      var (hue, sat) = ConvertRGBtoHS(red, green, blue);

      var response = await HueApiPut($"lights/{id}/state", new { on = true, hue, sat, bri = 255 });

      return response;
   }

   static (int, int) ConvertRGBtoHS(int r, int g, int b)
   {
      // Normalize RGB values to the range 0-1
      var r_norm = r / 255.0;
      var g_norm = g / 255.0;
      var b_norm = b / 255.0;

      var max = Math.Max(Math.Max(r_norm, g_norm), b_norm);
      var min = Math.Min(Math.Min(r_norm, g_norm), b_norm);
      var delta = max - min;

      double h;
      if (max == r_norm)
      {
         h = (60 * ((g_norm - b_norm) / delta + 6)) % 360;
      }
      else if (max == g_norm)
      {
         h = 60 * ((b_norm - r_norm) / delta + 2);
      }
      else
      {
         h = 60 * ((r_norm - g_norm) / delta + 4);
      }

      if (max == 0)
      {
         h = 0;
      }

      // Saturation
      double s = (max == 0) ? 0 : (delta / max);

      // Normalize h to 0-65535 and s to 0-254 as per Philips Hue API documentation
      int hue = (int)(h / 360.0 * 65535);
      int sat = (int)(s * 254);

      return (hue, sat);
   }

}

public class HueLightModel
{
   [JsonPropertyName("name")]
   public string Name { get; set; }

   [JsonPropertyName("state")]
   public HueLightState State { get; set; }
}

public class HueLightState
{
   [JsonPropertyName("on")]
   public bool On { get; set; }
}

public class HueBridge
{
   [JsonPropertyName("id")]
   public string Id { get; set; }

   [JsonPropertyName("internalipaddress")]
   public string InternalIpAddress { get; set; }
}

public class LightModel
{
   [JsonPropertyName("id")]
   public int Id { get; set; }

   [JsonPropertyName("name")]
   public string Name { get; set; }

   [JsonPropertyName("is_on")]
   public bool? IsOn { get; set; }
}