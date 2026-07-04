using System.Text.Json;
using System.IO;
using System.Collections.Generic;

namespace Library;

public static class StateHelper
{
    public static HashSet<long> LoadState(string stateFilePath)
    {
        if (!File.Exists(stateFilePath)) return new HashSet<long>();
        string json = File.ReadAllText(stateFilePath);
        return JsonSerializer.Deserialize<HashSet<long>>(json) ?? new HashSet<long>();
    }
    public static void SaveState(string stateFilePath, HashSet<long> state)
    {
        string json = JsonSerializer.Serialize(state);
        File.WriteAllText(stateFilePath, json);
    }
}
