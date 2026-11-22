using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;

namespace dnSpy.CallGraph.Updates {
	sealed record update_release(Version version, Uri download) {
		public string display_version => "v" + update_protocol.format(this.version);
		public bool is_dll => this.download.AbsolutePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
	}
	static class update_protocol {
		public const string feed = "https://pastebin.com/raw/DB8BX6zq";
		public const string repository = "Quantum54554545/dnSpy.Extension.CallGraph";
		public const int maximum_dll_bytes = 32 * 1024 * 1024;
		public static string installed_path => typeof(update_protocol).Assembly.Location;
		public static Version current_version => update_protocol.parse_version(typeof(update_protocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
		public static Version parse_version(string text) {
			text = text.Trim().TrimStart('\uFEFF');
			if (!Regex.IsMatch(text, @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z", RegexOptions.CultureInvariant) || !Version.TryParse(text.TrimStart('v'), out var version)) throw new InvalidDataException("Invalid release version: " + text);
			return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
		}
		public static string format(Version version) => version.Revision > 0 ? version.ToString(4) : version.ToString(3);
		public static update_release parse_feed(string text) {
			var lines = text.Trim().Split('\n').Select(line => line.Trim()).ToArray();
			if (lines.Length != 2) throw new InvalidDataException("The update feed must contain exactly two lines.");
			var version = update_protocol.parse_version(lines[0]);
			if (!Uri.TryCreate(lines[1], UriKind.Absolute, out var uri) || !update_protocol.is_release_asset(uri, version)) throw new InvalidDataException("Expected a direct HTTPS release asset in " + update_protocol.repository);
			return new update_release(version, uri);
		}
		public static bool is_release_asset(Uri uri, Version version) {
			if (!update_protocol.is_https(uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
			string prefix = "/" + update_protocol.repository + "/releases/download/";
			if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
			var parts = uri.AbsolutePath.Substring(prefix.Length).Split('/');
			if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
			try { return update_protocol.parse_version(parts[0]) == version; } catch (InvalidDataException) { return false; }
		}
		static bool is_https(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
		public static bool is_download_hop(Uri uri, Version version) => update_protocol.is_https(uri) && (update_protocol.is_release_asset(uri, version) || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com");
		public static void validate_dll(string path, Version expected) {
			if (new FileInfo(path).Length is <= 0 or > update_protocol.maximum_dll_bytes) throw new InvalidDataException("Invalid update DLL size.");
			using var candidate = ModuleDefMD.Load(path);
			if (candidate.Assembly is null || candidate.Assembly.Name != typeof(update_protocol).Assembly.GetName().Name || !candidate.IsILOnly) throw new InvalidDataException("This is not a managed Call Graph extension DLL.");
			bool compatible = candidate.Machine switch {
				dnlib.PE.Machine.I386 => !candidate.Is32BitRequired || RuntimeInformation.ProcessArchitecture == Architecture.X86,
				dnlib.PE.Machine.AMD64 => RuntimeInformation.ProcessArchitecture == Architecture.X64,
				dnlib.PE.Machine.ARM64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
				_ => false,
			};
			if (!compatible) throw new InvalidDataException("The DLL architecture does not match the running dnSpy process.");
			string attribute_value(string name) => candidate.Assembly.CustomAttributes.FirstOrDefault(attribute => attribute.TypeFullName == name)?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
			if (update_protocol.parse_version(attribute_value("System.Reflection.AssemblyInformationalVersionAttribute")) != expected || update_protocol.parse_version(attribute_value("System.Reflection.AssemblyFileVersionAttribute")) != expected) throw new InvalidDataException("The downloaded DLL version does not match the feed.");
			string framework = typeof(update_protocol).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName;
			if (attribute_value("System.Runtime.Versioning.TargetFrameworkAttribute") != framework) throw new InvalidDataException("The update targets a different .NET runtime.");
		}
		public static string hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
		public static string installation_key(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Substring(0, 24);
	}
}
