using System;
using System.IO;
using System.IO.Compression;

class Program {
    static void Main() {
        byte[] data = File.ReadAllBytes("/tmp/test6.bin");
        byte[] inner = new byte[data.Length - 8];
        Buffer.BlockCopy(data, 8, inner, 0, inner.Length);
        
        using (var ms = new MemoryStream(inner))
        using (var gz = new GZipStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream()) {
            gz.CopyTo(outMs);
            byte[] decomp = outMs.ToArray();
            File.WriteAllBytes("/tmp/test6_decomp.bin", decomp);
        }
    }
}
