using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SonosCast;

public sealed record SonosDevice(string Ip, string RoomName, string ModelName, string Uuid)
{
    public override string ToString() => $"{RoomName}  ({ModelName}, {Ip})";
}

/// <summary>Finds Sonos speakers and drives them through their local UPnP API on port 1400.</summary>
public static class Sonos
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // ---------- discovery ----------

    public static async Task<List<SonosDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        var ips = new HashSet<string>();
        foreach (var ip in await SsdpAsync(ct)) ips.Add(ip);
        // SSDP replies are often eaten by the Windows firewall, so also probe every local /24 for port 1400.
        if (ips.Count == 0)
            foreach (var ip in await ScanSubnetsAsync(ct)) ips.Add(ip);

        var devices = await Task.WhenAll(ips.Select(ip => DescribeAsync(ip, ct)));
        return devices.Where(d => d != null)
                      .Select(d => d!)
                      .GroupBy(d => d.Uuid).Select(g => g.First())
                      .OrderBy(d => d.RoomName, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    static async Task<List<string>> SsdpAsync(CancellationToken ct)
    {
        var found = new List<string>();
        const string msg = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n" +
                           "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n";
        try
        {
            using var udp = new UdpClient(0);
            var bytes = Encoding.ASCII.GetBytes(msg);
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            for (int i = 0; i < 2; i++) await udp.SendAsync(bytes, target, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            while (!cts.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.ASCII.GetString(r.Buffer);
                if (text.Contains("Sonos", StringComparison.OrdinalIgnoreCase) || text.Contains("ZonePlayer"))
                    found.Add(r.RemoteEndPoint.Address.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return found;
    }

    static async Task<List<string>> ScanSubnetsAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength < 16) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                for (int i = 1; i < 255; i++) candidates.Add($"{b[0]}.{b[1]}.{b[2]}.{i}");
            }
        }

        var open = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(128);
        await Task.WhenAll(candidates.Distinct().Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(600);
                await tcp.ConnectAsync(ip, 1400, cts.Token);
                open.Add(ip);
            }
            catch { }
            finally { gate.Release(); }
        }));
        return open.ToList();
    }

    public static async Task<SonosDevice?> DescribeAsync(string ip, CancellationToken ct = default)
    {
        try
        {
            var xml = await Http.GetStringAsync($"http://{ip}:1400/xml/device_description.xml", ct);
            string Get(string tag) => Regex.Match(xml, $"<{tag}>(.*?)</{tag}>").Groups[1].Value;
            var uuid = Get("UDN").Replace("uuid:", "");
            if (!uuid.StartsWith("RINCON_")) return null;
            return new SonosDevice(ip, WebUtility.HtmlDecode(Get("roomName")), WebUtility.HtmlDecode(Get("modelName")), uuid);
        }
        catch { return null; }
    }

    // ---------- control ----------

    const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    const string Rendering = "urn:schemas-upnp-org:service:RenderingControl:1";

    static async Task<string> SoapAsync(string ip, string path, string service, string action, string args)
    {
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:1400{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPACTION", $"\"{service}#{action}\"");
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var code = Regex.Match(text, "<errorCode>(\\d+)</errorCode>").Groups[1].Value;
            throw new InvalidOperationException($"Sonos {action} failed (HTTP {(int)resp.StatusCode}{(code != "" ? $", UPnP error {code}" : "")})");
        }
        return text;
    }

    /// <summary>
    /// Returns the IP of the group coordinator for this speaker. Playback commands must go to the
    /// coordinator, so a grouped speaker plays the stream along with the rest of its group.
    /// </summary>
    public static async Task<string> GetCoordinatorIpAsync(SonosDevice dev)
    {
        try
        {
            var resp = await SoapAsync(dev.Ip, "/ZoneGroupTopology/Control", "urn:schemas-upnp-org:service:ZoneGroupTopology:1", "GetZoneGroupState", "");
            var stateXml = WebUtility.HtmlDecode(Regex.Match(resp, "<ZoneGroupState>(.*?)</ZoneGroupState>", RegexOptions.Singleline).Groups[1].Value);
            var doc = XDocument.Parse(stateXml);
            foreach (var group in doc.Descendants("ZoneGroup"))
            {
                var members = group.Elements("ZoneGroupMember").ToList();
                if (!members.Any(m => (string?)m.Attribute("UUID") == dev.Uuid)) continue;
                var coord = (string?)group.Attribute("Coordinator");
                var loc = members.FirstOrDefault(m => (string?)m.Attribute("UUID") == coord)?.Attribute("Location")?.Value;
                if (loc != null) return new Uri(loc).Host;
            }
        }
        catch { }
        return dev.Ip;
    }

    public static async Task PlayStreamAsync(string ip, string streamUrl, string title)
    {
        var t = SecurityElement.Escape(title);
        var didl = "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
                   "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
                   $"<item id=\"pcaudio\" parentID=\"0\" restricted=\"true\"><dc:title>{t}</dc:title>" +
                   "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
                   $"<res protocolInfo=\"http-get:*:audio/wav:*\">{SecurityElement.Escape(streamUrl)}</res></item></DIDL-Lite>";
        await SoapAsync(ip, "/MediaRenderer/AVTransport/Control", AvTransport, "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>{SecurityElement.Escape(streamUrl)}</CurrentURI><CurrentURIMetaData>{SecurityElement.Escape(didl)}</CurrentURIMetaData>");
        await SoapAsync(ip, "/MediaRenderer/AVTransport/Control", AvTransport, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
    }

    public static async Task StopAsync(string ip)
    {
        try { await SoapAsync(ip, "/MediaRenderer/AVTransport/Control", AvTransport, "Stop", "<InstanceID>0</InstanceID>"); }
        catch { /* already stopped */ }
    }

    public static async Task<string> GetTransportStateAsync(string ip)
    {
        var r = await SoapAsync(ip, "/MediaRenderer/AVTransport/Control", AvTransport, "GetTransportInfo", "<InstanceID>0</InstanceID>");
        return Regex.Match(r, "<CurrentTransportState>(.*?)</CurrentTransportState>").Groups[1].Value;
    }

    public static async Task<int> GetVolumeAsync(string ip)
    {
        var r = await SoapAsync(ip, "/MediaRenderer/RenderingControl/Control", Rendering, "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
        return int.TryParse(Regex.Match(r, "<CurrentVolume>(\\d+)</CurrentVolume>").Groups[1].Value, out var v) ? v : 0;
    }

    public static Task SetVolumeAsync(string ip, int volume) =>
        SoapAsync(ip, "/MediaRenderer/RenderingControl/Control", Rendering, "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>");

    /// <summary>The local IP address the speaker can reach us on (the interface that routes to it).</summary>
    public static string LocalIpFor(string remoteIp)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(remoteIp, 1400);
        return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
    }
}
