// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DynamicIsland.Core;
using QRCoder;

namespace DynamicIsland.Services;

/// <summary>
/// Comparte archivos con el celular por la red Wi-Fi local: un servidor HTTP mínimo
/// y temporal con un enlace secreto, que se muestra como código QR. Se apaga solo a los 15 minutos.
/// </summary>
public sealed class PhoneShare
{
    public static PhoneShare Instance { get; } = new();

    TcpListener? _listener;
    string[] _files = Array.Empty<string>();
    string _token = "";
    DateTime _expires;

    public string? Url { get; private set; }
    public bool IsRunning => _listener != null;

    /// <returns>El enlace, o null si no hay una red local disponible.</returns>
    public string? Start(string[] files)
    {
        Stop();
        string? ip = LocalIPv4();
        if (ip == null || files.Length == 0) return null;

        _files = files;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _expires = DateTime.Now.AddMinutes(15);

        for (int port = 8642; port < 8662; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                _listener = l;
                Url = $"http://{ip}:{port}/{_token}";
                _ = AcceptLoop(l);
                _ = Task.Delay(TimeSpan.FromMinutes(15)).ContinueWith(_ => { if (_listener == l) Stop(); });
                return Url;
            }
            catch (SocketException) { }
        }
        return null;
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Url = null;
    }

    async Task AcceptLoop(TcpListener listener)
    {
        while (_listener == listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            if (DateTime.Now > _expires) { client.Dispose(); Stop(); return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var cts = new CancellationTokenSource(10000);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(cts.Token);
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cts.Token))) { }
                var parts = requestLine?.Split(' ');
                string path = parts != null && parts.Length >= 2 ? parts[1] : "/";

                string root = "/" + _token;
                if (path == root || path == root + "/")
                {
                    await WriteText(stream, "200 OK", "text/html; charset=utf-8", Page(root));
                    return;
                }
                if (path.StartsWith(root + "/f/") && int.TryParse(path[(root.Length + 3)..], out int index)
                    && index >= 0 && index < _files.Length && File.Exists(_files[index]))
                {
                    await SendFile(stream, _files[index]);
                    return;
                }
                await WriteText(stream, "404 Not Found", "text/plain; charset=utf-8", "Enlace no válido o vencido.");
            }
            catch (Exception ex) { Log.Error("PhoneShare", ex); }
        }
    }

    string Page(string root)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Dynamic Island</title><style>body{margin:0;font-family:system-ui,sans-serif;background:#0b0d10;color:#fff;padding:24px}");
        sb.Append("h1{font-size:20px;margin:0 0 18px}a{display:block;background:" + DynamicIsland.Core.UI.AccentHex + ";color:#000;text-decoration:none;padding:16px;border-radius:14px;margin:0 0 12px;font-weight:600;word-break:break-all}");
        sb.Append("small{display:block;font-weight:400;opacity:.7;margin-top:4px}</style></head><body><h1>Archivos compartidos</h1>");
        for (int i = 0; i < _files.Length; i++)
        {
            if (!File.Exists(_files[i])) continue;
            var info = new FileInfo(_files[i]);
            sb.Append($"<a href=\"{root}/f/{i}\">{WebUtility.HtmlEncode(info.Name)}<small>{UI.Bytes(info.Length)}</small></a>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    static async Task WriteText(NetworkStream stream, string status, string type, string body)
    {
        byte[] data = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(data);
    }

    static async Task SendFile(NetworkStream stream, string path)
    {
        var info = new FileInfo(path);
        string ascii = new string(info.Name.Select(c => c < 128 && c != '"' ? c : '_').ToArray());
        string head = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                      $"Content-Length: {info.Length}\r\n" +
                      $"Content-Disposition: attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(info.Name)}\r\n" +
                      "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await using var file = File.OpenRead(path);
        await file.CopyToAsync(stream);
    }

    /// <summary>IP privada del adaptador conectado a la red local (Wi-Fi o Ethernet).</summary>
    static string? LocalIPv4()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = n.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] b = a.Address.GetAddressBytes();
                bool isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                if (isPrivate) return a.Address.ToString();
            }
        }
        return null;
    }

    /// <summary>Código QR del enlace como imagen.</summary>
    public static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
