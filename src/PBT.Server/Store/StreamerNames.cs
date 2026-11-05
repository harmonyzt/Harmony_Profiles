using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PBT.Server
{
    public sealed partial class PopulationStore
    {
        private static readonly Regex StreamerName = new Regex(@"^[\w\-\.]{1,32}$", RegexOptions.Compiled);

        // A streamer name has to belong to a player. Unknown names go into Bot Callsigns' own list, which it only reads on server start.
        public (bool Ok, bool NeedsRestart, string Message) CheckStreamerName(string raw, string side)
        {
            string name = raw.Trim();
            if (!StreamerName.IsMatch(name)) return (false, false, "Names are 1-32 letters, digits, _ - or .");
            lock (gate)
            {
                if (!Ready) return (false, false, "PBT has not finished loading.");
                if (population.Careers.Values.Any(c => !c.Retired && (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                                      || c.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    return (true, false, $"{name} counts as a streamer once you save the simulation policy.");
            }

            string folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!, "BotCallsigns");
            string file = Path.Combine(folder, "nameData", "userDefinedNames.json");
            if (!Directory.Exists(folder)) return (false, false, $"No player is named {name}. Install Bot Callsigns to add new names.");
            string key = side.Equals("Bear", StringComparison.OrdinalIgnoreCase) ? "BearNames" : "UsecNames";
            try
            {
                string configFile = Path.Combine(folder, "config.json");
                if (File.Exists(configFile) && JsonNode.Parse(File.ReadAllText(configFile))?["EnableUserDefinedNames"]?.GetValue<bool>() == false)
                    return (false, false, $"No player is named {name}. Turn on EnableUserDefinedNames in Bot Callsigns' config.json to add it.");
                JsonObject names = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? new JsonObject() : new JsonObject();
                if (names[key] is not JsonArray list) names[key] = list = new JsonArray();
                if (!list.Any(n => string.Equals(n?.ToString(), name, StringComparison.OrdinalIgnoreCase))) list.Add(name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.Write(file, names.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return (false, false, "Could not update Bot Callsigns' userDefinedNames.json: " + ex.Message);
            }

            return (true, true,
                $"{name} was added to Bot Callsigns as a {(key == "BearNames" ? "BEAR" : "USEC")} name. Save the simulation policy, then restart the server so the player exists.");
        }
    }
}
