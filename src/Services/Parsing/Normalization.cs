#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace MyM365AgentDecommision.Bot.Services
{
    public static class Normalization
    {
        public static string NormalizeRegion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var key = raw.Trim().ToLowerInvariant();

            static string Compact(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray());

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["west us"]   = "westus",
                ["west-us"]   = "westus",
                ["westus"]    = "westus",
                ["west us 2"] = "westus2",
                ["west-us-2"] = "westus2",
                ["westus2"]   = "westus2",

                ["east us"]   = "eastus",
                ["east-us"]   = "eastus",
                ["eastus"]    = "eastus",
                ["east us 2"] = "eastus2",
                ["eastus2"]   = "eastus2",

                ["west europe"] = "westeurope",
                ["westeurope"]  = "westeurope",
                ["north europe"]= "northeurope",
                ["northeurope"] = "northeurope",

                ["southeast asia"] = "southeastasia",
                ["southeastasia"]  = "southeastasia",
                ["east asia"]      = "eastasia",
                ["eastasia"]       = "eastasia"
            };

            if (map.TryGetValue(key, out var v)) return v;

            var compact = Compact(key);
            if (map.TryGetValue(compact, out v)) return v;

            return compact; // best-effort canonical
        }
    }
}
