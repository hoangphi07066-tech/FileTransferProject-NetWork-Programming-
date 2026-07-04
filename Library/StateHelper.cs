namespace Library;
public static class StateHelper {
    public static void PrintProgress(string name, long up, long total) {
        Console.Write($"\r[TCP Stream] {name}: {(double)up/total*100:F2}% ({up}/{total} B)");
        if (up == total) Console.WriteLine("\n[OK] Truyền dữ liệu hoàn tất.");
    }
}