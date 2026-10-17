using System.Text.Json;
using SegmentScanner.Contracts;
using SegmentScanner.Engine.Credits;
using SegmentScanner.Engine.Media;

namespace SegmentScanner.Engine.Jellyfin;

/// <summary>
/// Discovers local node capabilities without changing the node's selected policy.
/// </summary>
public static class NodeCapabilities
{
    private const string OriginalGpuDetectorId = "introskipper-credits-gpu-v1";
    private static readonly byte[] InferenceProbeImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAQElEQVRIDbXBAQEAAABAIP6ftkLJTGYyk5nMZCYzmclMZjKTmcxkJjOZyUxmMpOZzGQmM5nJTGYyk5nMZCYzmQXUMAAh/li15wAAAABJRU5ErkJggg==");

    /// <summary>
    /// Discovers the local hardware and executable credits recipes without contacting a server.
    /// </summary>
    /// <param name="configuration">The local worker configuration used to locate bundled tools.</param>
    /// <param name="token">The token used to cancel local capability probes.</param>
    /// <returns>The local node runtime inventory and available credits recipes.</returns>
    public static async Task<NodeRuntimeInformation> DiscoverAsync(WorkerConfiguration configuration, CancellationToken token)
    {
        var hardware = await DiscoverHardwareAsync(token).ConfigureAwait(false);
        var recipes = new List<NodeCreditRecipe>();
        var originalCpu = OriginalConfiguration(configuration, CreditVideoDecoder.Cpu, useGpu: false);
        recipes.Add(new NodeCreditRecipe
        {
            Id = "introskipper-credits-cpu-v1",
            Method = CreditsDetectionMethod.IntroSkipper,
            InferenceBackend = CreditInferenceBackend.Cpu,
            VideoDecoder = CreditVideoDecoder.Cpu,
            ProfileHash = IntroSkipperCreditsDetector.ProfileHash(originalCpu),
        });

        var textCpu = TextConfiguration(configuration, CreditInferenceBackend.Cpu, CreditVideoDecoder.Cpu);
        var textCpuRecipe = await TryCreateTextRecipeAsync(textCpu, useGpu: false, token).ConfigureAwait(false);
        if (textCpuRecipe is not null)
        {
            recipes.Add(textCpuRecipe);
        }

        var decoder = await FindHardwareDecoderAsync(configuration, token).ConfigureAwait(false);
        if (decoder is { } selectedDecoder)
        {
            var originalGpu = OriginalConfiguration(configuration, selectedDecoder, useGpu: true);
            recipes.Add(new NodeCreditRecipe
            {
                Id = OriginalGpuDetectorId,
                Method = CreditsDetectionMethod.IntroSkipper,
                UseGpu = true,
                InferenceBackend = CreditInferenceBackend.Cpu,
                VideoDecoder = selectedDecoder,
                ProfileHash = IntroSkipperCreditsDetector.ProfileHash(originalGpu),
            });

            var textGpu = TextConfiguration(configuration, CreditInferenceBackend.OpenClGpu, selectedDecoder);
            var textGpuRecipe = await TryCreateTextRecipeAsync(textGpu, useGpu: true, token).ConfigureAwait(false);
            if (textGpuRecipe is not null)
            {
                recipes.Add(textGpuRecipe);
            }
        }

        return new NodeRuntimeInformation
        {
            Version = typeof(NodeCapabilities).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Hardware = hardware,
            CreditsRecipes = recipes,
            SuggestedPathMappings = [.. configuration.Paths.Mappings],
            SuggestedConcurrentFiles = configuration.Resources.ConcurrentFiles,
        };
    }

    internal static async Task<NodeHardwareInformation> DiscoverHardwareAsync(CancellationToken token)
    {
        var operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var cpuName = default(string);
        long? memoryBytes = null;
        var gpus = new List<NodeGpuInformation>();

        if (OperatingSystem.IsWindows())
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var details = await TryRunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", "$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name); $memory = (Get-CimInstance Win32_ComputerSystem | Select-Object -First 1 -ExpandProperty TotalPhysicalMemory); [Console]::WriteLine($cpu); [Console]::WriteLine($memory); Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,AdapterRAM | ConvertTo-Json -Compress"], token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(details))
            {
                ReadWindowsHardware(details, ref cpuName, ref memoryBytes, gpus);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            (cpuName, memoryBytes) = ReadLinuxProcessorAndMemory();
            var pci = await TryRunAsync("lspci", ["-nn"], token).ConfigureAwait(false);
            if (pci is not null)
            {
                foreach (var line in pci.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("VGA compatible controller:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("3D controller:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Display controller:", StringComparison.OrdinalIgnoreCase))
                    {
                        gpus.Add(new NodeGpuInformation(line.Trim()));
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            cpuName = await TryRunAsync("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"], token).ConfigureAwait(false);
            if (long.TryParse(await TryRunAsync("/usr/sbin/sysctl", ["-n", "hw.memsize"], token).ConfigureAwait(false), out var memory))
            {
                memoryBytes = memory;
            }
        }

        return new NodeHardwareInformation
        {
            OperatingSystem = operatingSystem,
            CpuName = string.IsNullOrWhiteSpace(cpuName) ? null : cpuName.Trim(),
            LogicalProcessors = Environment.ProcessorCount,
            MemoryBytes = memoryBytes,
            Gpus = gpus,
        };
    }

    private static WorkerConfiguration OriginalConfiguration(WorkerConfiguration configuration, CreditVideoDecoder decoder, bool useGpu)
        => configuration with
        {
            Credits = configuration.Credits with
            {
                Method = CreditsDetectionMethod.IntroSkipper,
                UseGpu = useGpu,
                InferenceBackend = CreditInferenceBackend.Cpu,
                VideoDecoder = decoder,
            },
        };

    private static WorkerConfiguration TextConfiguration(WorkerConfiguration configuration, CreditInferenceBackend backend, CreditVideoDecoder decoder)
        => configuration with
        {
            Credits = configuration.Credits with
            {
                Method = CreditsDetectionMethod.TextAware,
                UseGpu = backend == CreditInferenceBackend.OpenClGpu,
                InferenceBackend = backend,
                VideoDecoder = decoder,
            },
        };

    private static async Task<NodeCreditRecipe?> TryCreateTextRecipeAsync(WorkerConfiguration configuration, bool useGpu, CancellationToken token)
    {
        if (configuration.Credits.ModelManifest is null || !File.Exists(configuration.Credits.ModelManifest))
        {
            return null;
        }

        try
        {
            await using var helper = await InferenceHelper.StartAsync(configuration, token).ConfigureAwait(false);
            var probeDirectory = Path.Combine(configuration.Paths.CacheDirectory, "frames");
            Directory.CreateDirectory(probeDirectory);
            var probePath = Path.Combine(probeDirectory, ".node-capabilities-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await File.WriteAllBytesAsync(probePath, InferenceProbeImage, token).ConfigureAwait(false);
                var evidence = await helper.InferAsync([new ExtractedFrame("capability", 0, probePath)], token).ConfigureAwait(false);
                if (evidence.Count != 1 || evidence[0].Features is null || evidence[0].Error is not null)
                {
                    return null;
                }
            }
            finally
            {
                File.Delete(probePath);
            }

            return new NodeCreditRecipe
            {
                Id = useGpu ? configuration.Credits.ProfileId + "-gpu" : configuration.Credits.ProfileId,
                Method = CreditsDetectionMethod.TextAware,
                UseGpu = useGpu,
                InferenceBackend = configuration.Credits.InferenceBackend,
                VideoDecoder = configuration.Credits.VideoDecoder,
                ProfileHash = new CreditsDetector(configuration).ProfileHash(),
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<CreditVideoDecoder?> FindHardwareDecoderAsync(WorkerConfiguration configuration, CancellationToken token)
    {
        foreach (var decoder in new[] { CreditVideoDecoder.NvidiaNvdec, CreditVideoDecoder.IntelQuickSync })
        {
            if (await CanInitializeDecoderAsync(configuration, decoder, token).ConfigureAwait(false))
            {
                return decoder;
            }
        }

        return null;
    }

    private static async Task<bool> CanInitializeDecoderAsync(WorkerConfiguration configuration, CreditVideoDecoder decoder, CancellationToken token)
    {
        try
        {
            var available = await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg, ["-hide_banner", "-decoders"], 20, token, 128 * 1024).ConfigureAwait(false);
            var decoderNames = available.StandardOutput.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length > 1).Select(fields => fields[1]).ToHashSet(StringComparer.Ordinal);
            var required = decoder == CreditVideoDecoder.NvidiaNvdec ? new[] { "h264_cuvid", "hevc_cuvid", "av1" } : new[] { "h264_qsv", "hevc_qsv", "av1_qsv" };
            if (required.Any(name => !decoderNames.Contains(name)))
            {
                return false;
            }

            var device = decoder == CreditVideoDecoder.NvidiaNvdec ? "cuda=segmentscanner" : "qsv=segmentscanner,child_device_type=d3d11va";
            await ProcessRunner.RunAsync(configuration.Paths.Ffmpeg,
                ["-hide_banner", "-nostdin", "-init_hw_device", device, "-f", "lavfi", "-i", "color=size=32x32:duration=0.04", "-frames:v", "1", "-f", "null", "-"], 30, token, 128 * 1024).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string?> TryRunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        try
        {
            return (await ProcessRunner.RunAsync(executable, arguments, 10, token, 128 * 1024).ConfigureAwait(false)).StandardOutput.Trim();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReadWindowsHardware(string details, ref string? cpuName, ref long? memoryBytes, ICollection<NodeGpuInformation> gpus)
    {
        var lines = details.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var jsonStart = Array.FindIndex(lines, line => line.StartsWith('{') || line.StartsWith('['));
        var plain = jsonStart < 0 ? lines : lines[..jsonStart];
        if (plain.Length < 2)
        {
            return;
        }

        cpuName = plain[0];
        if (long.TryParse(plain[1], out var physicalMemory))
        {
            memoryBytes = physicalMemory;
        }

        if (jsonStart < 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(string.Join('\n', lines[jsonStart..]));
            IEnumerable<JsonElement> controllers = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray() : [document.RootElement];
            foreach (var controller in controllers)
            {
                if (!controller.TryGetProperty("Name", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                {
                    continue;
                }

                var driver = controller.TryGetProperty("DriverVersion", out var driverValue) ? driverValue.GetString() : null;
                long? adapterMemory = controller.TryGetProperty("AdapterRAM", out var memoryValue) && memoryValue.ValueKind == JsonValueKind.Number
                    && memoryValue.TryGetInt64(out var adapterRam) && adapterRam > 0 ? adapterRam : null;
                gpus.Add(new NodeGpuInformation(name.GetString()!, driver, adapterMemory));
            }
        }
        catch (JsonException)
        {
            // Inventory stays available when a local driver returns malformed WMI JSON.
        }
    }

    private static (string? CpuName, long? MemoryBytes) ReadLinuxProcessorAndMemory()
    {
        try
        {
            var cpuName = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[^1].Trim();
            var memoryLine = File.ReadLines("/proc/meminfo").FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.Ordinal));
            var kilobytes = memoryLine is null ? 0 : long.TryParse(new string(memoryLine.Where(char.IsDigit).ToArray()), out var parsed) ? parsed : 0;
            return (cpuName, kilobytes > 0 ? checked(kilobytes * 1024) : null);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
