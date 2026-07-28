using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GlavaSharp;

/// <summary>
///     Best-effort GPU listing for --list-gpus / --gpu &lt;index&gt;. This is only
///     for picking a DRI_PRIME index on a multi-GPU system -- it doesn't need
///     to be exhaustive or perfectly ordered, just enough to tell two GPUs
///     apart before you commit to a full rebuild+run cycle. Index order here
///     matches Mesa's own DRI_PRIME=&lt;N&gt; ordering (PCI enumeration order of
///     /sys/class/drm/cardN), not necessarily "which one is the dGPU".
/// </summary>
public static class GpuEnumerator
{
    public static List<string> List()
    {
        var fromSysfs = TryListFromSysfs();
        if (fromSysfs.Count > 0) return fromSysfs;

        var fromLspci = TryListFromLspci();
        return fromLspci.Count > 0 ? fromLspci : [];
    }

    private static List<string> TryListFromSysfs()
    {
        var result = new List<string>();
        var driver = "?";
        try
        {
            const string drmDir = "/sys/class/drm";
            if (!Directory.Exists(drmDir)) return result;

            // cardN entries (not renderD1xx / cardN-<connector>) map 1:1 to
            // GPUs in PCI enumeration order, same order DRI_PRIME indexes.
            var cards = Directory.GetDirectories(drmDir, "card*")
                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^card\d+$"))
                .OrderBy(d => int.Parse(Path.GetFileName(d).Substring(4)))
                .ToList();

            foreach (var card in cards)
            {
                var devDir = Path.Combine(card, "device");
                var vendorPath = Path.Combine(devDir, "vendor");
                var devicePath = Path.Combine(devDir, "device");
                var driverLink = Path.Combine(devDir, "driver");

                var vendor = File.Exists(vendorPath) ? File.ReadAllText(vendorPath).Trim() : "?";
                var device = File.Exists(devicePath) ? File.ReadAllText(devicePath).Trim() : "?";
                var vendorName = VendorName(vendor);
                try
                {
                    if (Directory.Exists(driverLink))
                    {
                        var target = new DirectoryInfo(driverLink).LinkTarget;
                        if (target != null) driver = Path.GetFileName(target);
                    }
                }
                catch
                {
                    /* best-effort */
                }

                result.Add($"{vendorName} (pci id {vendor}:{device}, driver {driver}) [{Path.GetFileName(card)}]");
            }
        }
        catch
        {
            // Best-effort only -- fall through to lspci or empty list.
        }

        return result;
    }

    private static string VendorName(string pciVendorId)
    {
        return pciVendorId switch
        {
            "0x8086" => "Intel",
            "0x10de" => "NVIDIA",
            "0x1002" => "AMD",
            _ => pciVendorId
        };
    }

    private static List<string> TryListFromLspci()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("lspci", "-mm")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return result;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            result
                .AddRange(from line in output
                        .Split('\n')
                    where line
                        .Contains("\"VGA compatible controller\"") || line
                        .Contains("\"3D controller\"") || line
                        .Contains("\"Display controller\"")
                    select line.Trim());
        }
        catch
        {
            // lspci not installed / not on PATH -- give up, caller returns empty.
        }

        return result;
    }
}