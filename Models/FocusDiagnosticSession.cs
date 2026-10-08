using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cwseo.NINA.ManualFocuser.Models {
    /// <summary>Private raw measurement archive; never publishes to NINA image history.</summary>
    public sealed class FocusDiagnosticSession {
        public string DirectoryPath { get; }
        private readonly bool enabled;
        private readonly Func<bool> stillEnabled;
        public bool IsEnabled => enabled && (stillEnabled?.Invoke() ?? true);
        private int sequence;
        public FocusDiagnosticSession(string mode, string root = null, bool enabled = false, Func<bool> stillEnabled = null) {
            this.enabled = enabled; this.stillEnabled = stillEnabled;
            if (!IsEnabled) return;
            root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "ManualFocuser", "FocusDiagnostics");
            DirectoryPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff'Z'") + "-" + mode + "-" + Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(DirectoryPath);
        }
        public async Task<string> SaveAsync(double[] pixels, int width, int height, object metadata) {
            if (!IsEnabled) return null;
            if (pixels == null || (long)width * height != pixels.Length || width < 1 || height < 1)
                throw new ArgumentException("Invalid diagnostic frame.");
            string name = (++sequence).ToString("D6");
            await Task.Run(() => {
                // Camera preview arrays are original unsigned 16-bit ADU, not display data.
                var cards = new[] { "SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2",
                    $"NAXIS1  = {width,20}", $"NAXIS2  = {height,20}", "BSCALE  =                    1", "BZERO   =                32768",
                    "COMMENT Raw focus frame; metadata in matching JSON. No display stretch.", "END" };
                using (var output = File.Create(Path.Combine(DirectoryPath,name+".fits"))) {
                    byte[] header = Encoding.ASCII.GetBytes(string.Concat(cards.Select(c=>c.PadRight(80))));
                    output.Write(header); output.Write(Enumerable.Repeat((byte)32,(2880-header.Length%2880)%2880).ToArray());
                    byte[] data = new byte[checked(pixels.Length*2)];
                    for(int i=0;i<pixels.Length;i++) {
                        if (!double.IsFinite(pixels[i]) || pixels[i]<0 || pixels[i]>65535 || pixels[i]!=Math.Truncate(pixels[i]))
                            throw new InvalidOperationException("Diagnostic frame is not unsigned 16-bit raw data.");
                        int signed=(int)pixels[i]-32768;
                        data[i*2]=(byte)(signed>>8); data[i*2+1]=(byte)signed;
                    }
                    output.Write(data); output.Write(new byte[(2880-data.Length%2880)%2880]);
                }
                File.WriteAllText(Path.Combine(DirectoryPath,name+".json"),JsonSerializer.Serialize(metadata,new JsonSerializerOptions{
                    WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
            });
            return name;
        }
        public Task EventAsync(object value) => !IsEnabled ? Task.CompletedTask : File.AppendAllTextAsync(Path.Combine(DirectoryPath,"measurements.jsonl"),
            JsonSerializer.Serialize(value,new JsonSerializerOptions{NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals})+Environment.NewLine);
    }
}
