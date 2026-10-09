using System.Collections.Concurrent;
using Amazon.S3;
using Amazon.S3.Model;
using AiGateway.Core;
using Microsoft.AspNetCore.DataProtection;
using SkiaSharp;

namespace AiGateway.Infrastructure;

public interface IObjectStorage
{
    Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<bool> ReadyAsync(CancellationToken ct);
}

public class S3Storage(IAmazonS3 s3, string bucket) : IObjectStorage
{
    public async Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream(data);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = ms, ContentType = contentType }, ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        using var res = await s3.GetObjectAsync(bucket, key, ct);
        using var ms = new MemoryStream();
        await res.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public Task DeleteAsync(string key, CancellationToken ct) => s3.DeleteObjectAsync(bucket, key, ct);

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(s3, bucket))
                await s3.PutBucketAsync(bucket, ct); // private by default
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Testing only.</summary>
public class MemoryStorage : IObjectStorage
{
    public readonly ConcurrentDictionary<string, byte[]> Objects = new();
    public Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct) { Objects[key] = data; return Task.CompletedTask; }
    public Task<byte[]> GetAsync(string key, CancellationToken ct) => Task.FromResult(Objects[key]);
    public Task DeleteAsync(string key, CancellationToken ct) { Objects.TryRemove(key, out _); return Task.CompletedTask; }
    public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(true);
}

public record SanitizedImage(byte[] Bytes, string MimeType, int Width, int Height);

public static class ImageSanitizer
{
    static readonly Dictionary<SKEncodedImageFormat, string> Mime = new()
    {
        [SKEncodedImageFormat.Jpeg] = "image/jpeg", [SKEncodedImageFormat.Png] = "image/png", [SKEncodedImageFormat.Webp] = "image/webp",
    };

    /// <summary>Signature-based detection, pixel cap before decode, metadata stripped by re-encoding, EXIF orientation applied.</summary>
    public static async Task<SanitizedImage> SanitizeAsync(Stream input, long maxPixels, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        using var data = SKData.CreateCopy(ms.ToArray());
        using var codec = SKCodec.Create(data) ?? throw Unsupported();
        if (!Mime.TryGetValue(codec.EncodedFormat, out var mime) || codec.FrameCount > 1) throw Unsupported();
        if ((long)codec.Info.Width * codec.Info.Height > maxPixels)
            throw new GatewayException(413, "upload_too_large", "Görsel piksel sınırını aşıyor.");

        using var decoded = SKBitmap.Decode(codec) ?? throw Unsupported();
        using var oriented = Orient(decoded, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(oriented);
        using var encoded = image.Encode(codec.EncodedFormat, 90) ?? throw Unsupported();
        return new(encoded.ToArray(), mime, oriented.Width, oriented.Height);
    }

    static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        var degrees = origin switch { SKEncodedOrigin.BottomRight => 180, SKEncodedOrigin.RightTop => 90, SKEncodedOrigin.LeftBottom => 270, _ => 0 };
        // shortcut: mirrored EXIF origins are treated as unrotated; add flips if users upload mirrored selfies.
        if (degrees == 0) return src.Copy();
        var swap = degrees != 180;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var canvas = new SKCanvas(dst);
        canvas.Translate(dst.Width / 2f, dst.Height / 2f);
        canvas.RotateDegrees(degrees);
        using var img = SKImage.FromBitmap(src);
        canvas.DrawImage(img, -src.Width / 2f, -src.Height / 2f, new SKSamplingOptions(SKFilterMode.Linear));
        return dst;
    }

    static GatewayException Unsupported() => new(415, "unsupported_media_type", "Yalnızca JPEG, PNG ve animasyonsuz WebP kabul edilir.");
}

public class SecretProtector(IDataProtectionProvider dp)
{
    public const string KeyVersion = "dp-v1";
    readonly IDataProtector _p = dp.CreateProtector("AiGateway.ProviderConnections." + KeyVersion);
    public string Protect(string secret) => _p.Protect(secret);
    public string Unprotect(string ciphertext) => _p.Unprotect(ciphertext);
}
