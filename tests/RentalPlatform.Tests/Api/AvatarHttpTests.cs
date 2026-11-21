using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RentalPlatform.Application.Abstractions;
using RentalPlatform.Infrastructure.Persistence;
using RentalPlatform.Infrastructure.Services;
using RentalPlatform.Tests.TestSupport;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace RentalPlatform.Tests.Api;

// PUT/DELETE /api/auth/me/avatar over HTTP (H-*, M-*): the real pipeline (auth, rate limiter, form limits,
// static files, security headers), the REAL LocalFileStorageService writing under the API's gitignored
// wwwroot/uploads/avatars, the real ImageSharp processor. Every test uses its own account (the avatar-update
// limiter is partitioned by user) and removes its directory afterwards.
[Collection("Integration")]
public sealed class AvatarHttpTests : IDisposable
{
    private readonly RentalPlatformWebAppFactory _factory;
    private readonly WebApplicationFactory<Program> _real;
    private readonly List<Guid> _users = new();
    private static int _ipCounter;

    public AvatarHttpTests(RentalPlatformWebAppFactory factory)
    {
        _factory = factory;
        _real = Real(_ => { });
    }

    private WebApplicationFactory<Program> Real(Action<IServiceCollection> extra) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService, LocalFileStorageService>();
            extra(services);
        }));

    public void Dispose()
    {
        var root = AvatarsRoot();
        foreach (var id in _users)
        {
            var dir = Path.Combine(root, id.ToString("N"));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _real.Dispose();
    }

    private string AvatarsRoot() =>
        Path.Combine(_real.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, "wwwroot", "uploads", "avatars");

    private sealed record Account(Guid Id, string Email, HttpClient Client);

    private async Task<Account> NewAccountAsync(WebApplicationFactory<Program>? host = null, bool blocked = false)
    {
        var id = Guid.NewGuid();
        var email = $"{id:N}@avatar.local";
        await _factory.SeedAsync(TestData.User(id, email, isBlocked: blocked));
        _users.Add(id);
        var client = (host ?? _real).CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, $"10.77.{(Interlocked.Increment(ref _ipCounter) >> 8) & 0xFF}.{_ipCounter & 0xFF}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtTokenHelper.GenerateToken(id, email));
        return new Account(id, email, client);
    }

    private static byte[] Png(int width = 600, int height = 400)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 120, 200, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Form(byte[]? bytes, string field = "file", string fileName = "me.png", string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        if (bytes is not null)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, field, fileName);
        }

        return form;
    }

    private static Task<HttpResponseMessage> PutAsync(Account account, HttpContent form) =>
        account.Client.PutAsync("/api/auth/me/avatar", form);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        return json.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private static async Task<string?> AvatarUrlAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        return json.GetProperty("avatarUrl").ValueKind == JsonValueKind.Null ? null : json.GetProperty("avatarUrl").GetString();
    }

    private string UserDir(Account account) => Path.Combine(AvatarsRoot(), account.Id.ToString("N"));

    private static int FileCount(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

    // ---- the happy path and what is served ---------------------------------------------------------------------

    [Fact]
    public async Task H1_Put_Returns_200_With_The_New_Url_Me_Agrees_And_The_File_Is_A_512_Jpeg_With_The_Right_Headers()
    {
        var a = await NewAccountAsync();

        var response = await PutAsync(a, Form(Png()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = await AvatarUrlAsync(response);
        Assert.Matches($"^/uploads/avatars/{a.Id:N}/[0-9a-f]{{32}}\\.jpg$", url);
        Assert.Equal(url, await AvatarUrlAsync(await a.Client.GetAsync("/api/auth/me")));

        var file = await a.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        var cache = file.Headers.CacheControl!.ToString();
        Assert.Equal("public, max-age=86400", cache);
        Assert.DoesNotContain("immutable", cache);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        var info = Image.Identify(await file.Content.ReadAsByteArrayAsync());
        Assert.Equal((512, 512), (info.Width, info.Height));
    }

    [Fact]
    public async Task H2_Replace_Serves_The_New_File_And_The_Old_Url_Is_404()
    {
        var a = await NewAccountAsync();
        var first = await AvatarUrlAsync(await PutAsync(a, Form(Png())));

        var second = await AvatarUrlAsync(await PutAsync(a, Form(Png(300, 300))));

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync(second)).StatusCode);
        Assert.Equal(1, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task H3_Delete_Returns_200_With_A_Null_Url_The_File_Is_404_And_A_Second_Delete_Is_Still_200()
    {
        var a = await NewAccountAsync();
        var url = await AvatarUrlAsync(await PutAsync(a, Form(Png())));

        var removed = await a.Client.DeleteAsync("/api/auth/me/avatar");
        var again = await a.Client.DeleteAsync("/api/auth/me/avatar");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(await AvatarUrlAsync(removed));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(await AvatarUrlAsync(await a.Client.GetAsync("/api/auth/me")));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task H4_Anonymous_Callers_Get_401_On_Both_Verbs()
    {
        var client = _real.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync("/api/auth/me/avatar", Form(Png()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/auth/me/avatar")).StatusCode);
    }

    [Fact]
    public async Task H5_A_Blocked_User_Gets_403_user_blocked_On_Both_Verbs_And_Nothing_Is_Written()
    {
        var a = await NewAccountAsync(blocked: true);

        var put = await PutAsync(a, Form(Png()));
        var delete = await a.Client.DeleteAsync("/api/auth/me/avatar");

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("auth.user_blocked", await ErrorCodeAsync(put));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal("auth.user_blocked", await ErrorCodeAsync(delete));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    // ---- request shape (M-*) ---------------------------------------------------------------------------------

    [Fact]
    public async Task M1_No_File_At_All_Is_400_avatar_missing()
    {
        var a = await NewAccountAsync();
        var form = new MultipartFormDataContent { { new StringContent("x"), "note" } };

        var response = await PutAsync(a, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_missing", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task M2_The_Wrong_Field_Name_Is_400_avatar_missing()
    {
        var a = await NewAccountAsync();

        var response = await PutAsync(a, Form(Png(), field: "image"));

        Assert.Equal("auth.avatar_missing", await ErrorCodeAsync(response));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task M3_Two_Files_Is_400_avatar_missing_And_Saves_Nothing()
    {
        var a = await NewAccountAsync();
        var form = Form(Png());
        form.Add(new ByteArrayContent(Png()), "file", "second.png");

        var response = await PutAsync(a, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_missing", await ErrorCodeAsync(response));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task M4_An_Empty_File_Is_400_avatar_empty()
    {
        var a = await NewAccountAsync();

        var response = await PutAsync(a, Form(Array.Empty<byte>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_empty", await ErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("hello, i am not an image", "note.png", "image/png")]
    [InlineData("GIF89a-not-allowed", "anim.gif", "image/gif")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", "x.svg", "image/svg+xml")]
    public async Task M5_The_Content_Decides_Not_The_Name_Or_Content_Type(string body, string name, string contentType)
    {
        var a = await NewAccountAsync();

        var response = await PutAsync(a, Form(System.Text.Encoding.ASCII.GetBytes(body), fileName: name, contentType: contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_invalid_type", await ErrorCodeAsync(response));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task M6_A_Png_Sent_As_dot_exe_With_Text_Plain_Is_Accepted_Because_Only_The_Bytes_Matter()
    {
        var a = await NewAccountAsync();

        var response = await PutAsync(a, Form(Png(), fileName: "../../evil.exe", contentType: "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches($"^/uploads/avatars/{a.Id:N}/[0-9a-f]{{32}}\\.jpg$", await AvatarUrlAsync(response));
    }

    [Fact]
    public async Task M7_A_Json_Body_Is_415_Not_A_500()
    {
        var a = await NewAccountAsync();

        var response = await a.Client.PutAsync("/api/auth/me/avatar", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task M8_An_Over_2_MiB_Image_Under_The_Multipart_Limit_Is_400_avatar_too_large()
    {
        var a = await NewAccountAsync();
        var big = new byte[2 * 1024 * 1024 + 4096];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;

        var response = await PutAsync(a, Form(big, contentType: "image/jpeg"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_too_large", await ErrorCodeAsync(response));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task M9_A_Pixel_Bomb_Under_2_MiB_Is_400_avatar_too_large()
    {
        var a = await NewAccountAsync();
        using var image = new Image<Rgba32>(5000, 5000, new Rgba32(1, 2, 3, 255)); // compresses to a few KB
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        Assert.True(stream.Length < 2 * 1024 * 1024);

        var response = await PutAsync(a, Form(stream.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.avatar_too_large", await ErrorCodeAsync(response));
    }

    // ---- size and form limits ----------------------------------------------------------------------------------

    // H-18: 2.6 MiB is under the 3 MiB request limit but over the 2.5 MiB multipart limit. The actual behaviour
    // is recorded in the assertion message; the UI maps both 413 and 400 to "too large".
    [Fact]
    public async Task H18_A_2_6_MiB_Body_Is_Rejected_With_413_Or_400_And_Nothing_Is_Saved()
    {
        var a = await NewAccountAsync();
        var big = new byte[(int)(2.6 * 1024 * 1024)];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;

        var response = await PutAsync(a, Form(big, contentType: "image/jpeg"));

        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"actual: {(int)response.StatusCode}");
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task H18b_A_3_5_MiB_Body_Is_Rejected_And_Nothing_Is_Saved()
    {
        var a = await NewAccountAsync();
        var big = new byte[(int)(3.5 * 1024 * 1024)];
        big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;

        var response = await PutAsync(a, Form(big, contentType: "image/jpeg"));

        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"actual: {(int)response.StatusCode}");
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    // H-19: ValueCountLimit = 8.
    [Fact]
    public async Task H19_More_Than_8_Form_Values_Are_Rejected_And_Nothing_Is_Saved()
    {
        var a = await NewAccountAsync();
        var form = Form(Png());
        for (var i = 0; i < 12; i++)
        {
            form.Add(new StringContent("v"), $"extra{i}");
        }

        var response = await PutAsync(a, form);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"actual: {(int)response.StatusCode}");
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    // ---- error mapping of the controller ----------------------------------------------------------------------

    [Fact]
    public async Task H15_A_Busy_Processor_Is_503_avatar_busy_With_Retry_After_5()
    {
        using var host = Real(services =>
        {
            services.RemoveAll<IAvatarImageProcessor>();
            services.AddSingleton<IAvatarImageProcessor>(new FakeAvatarImageProcessor { Status = AvatarProcessingStatus.Busy });
        });
        var a = await NewAccountAsync(host);

        var response = await PutAsync(a, Form(Png()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("auth.avatar_busy", await ErrorCodeAsync(response));
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task H16_Three_Lost_Cas_Attempts_Are_409_avatar_conflict_And_The_New_File_Is_Removed()
    {
        using var host = Real(services =>
        {
            services.RemoveAll<IAvatarStore>();
            services.AddScoped<IAvatarStore>(sp => new HookAvatarStore(new AvatarStore(sp.GetRequiredService<AppDbContext>()))
            {
                ForcedOutcome = _ => AvatarCasOutcome.Mismatch
            });
        });
        var a = await NewAccountAsync(host);

        var response = await PutAsync(a, Form(Png()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("auth.avatar_conflict", await ErrorCodeAsync(response));
        Assert.Equal(0, FileCount(UserDir(a)));
    }

    [Fact]
    public async Task H17_A_Form_Field_Naming_Another_User_Changes_Nothing_Of_Theirs()
    {
        var a = await NewAccountAsync();
        var b = await NewAccountAsync();
        var form = Form(Png());
        form.Add(new StringContent(b.Id.ToString()), "userId");

        var response = await PutAsync(a, form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await AvatarUrlAsync(await b.Client.GetAsync("/api/auth/me")));
        Assert.Equal(0, FileCount(UserDir(b)));
    }

    // ---- rate limit (H-13b) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task H13b_Put_And_Delete_Share_One_Counter_Of_10_Per_Hour_Per_User()
    {
        var a = await NewAccountAsync();
        var other = await NewAccountAsync();
        var png = Png(64, 64);

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(a, Form(png))).StatusCode);
        }

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await a.Client.DeleteAsync("/api/auth/me/avatar")).StatusCode);
        }

        var putOver = await PutAsync(a, Form(png));
        var deleteOver = await a.Client.DeleteAsync("/api/auth/me/avatar");

        Assert.Equal(HttpStatusCode.TooManyRequests, putOver.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, deleteOver.StatusCode);
        Assert.Equal(string.Empty, await putOver.Content.ReadAsStringAsync()); // bare 429
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(other, Form(png))).StatusCode); // another account is unaffected
    }

    // ---- static files -------------------------------------------------------------------------------------------

    [Fact]
    public async Task H20_Only_Jpg_Is_Served_From_The_Avatars_Tree()
    {
        var a = await NewAccountAsync();
        var dir = UserDir(a);
        Directory.CreateDirectory(dir);
        var name = new string('e', 32);
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".png"), Png(8, 8));
        await File.WriteAllTextAsync(Path.Combine(dir, name + ".html"), "<script>1</script>");
        await File.WriteAllBytesAsync(Path.Combine(dir, name + ".jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        var client = _real.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/uploads/avatars/{a.Id:N}/{name}.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/uploads/avatars/{a.Id:N}/{name}.html")).StatusCode);
        var jpg = await client.GetAsync($"/uploads/avatars/{a.Id:N}/{name}.jpg"); // anonymous: avatars are public
        Assert.Equal(HttpStatusCode.OK, jpg.StatusCode);
        Assert.Equal("inline", jpg.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task H21_A_Missing_Avatar_Is_404()
    {
        var response = await _real.CreateClient().GetAsync($"/uploads/avatars/{Guid.NewGuid():N}/{new string('f', 32)}.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- account deletion wipes the directory (D-*, over HTTP) -----------------------------------------------------

    [Fact]
    public async Task H22_The_Contract_Url_Shape_Is_Stable_For_The_Frontend()
    {
        var a = await NewAccountAsync();

        var json = await JsonAsync(await PutAsync(a, Form(Png())));

        Assert.True(json.TryGetProperty("avatarUrl", out _));
        Assert.True(json.TryGetProperty("id", out _));
        Assert.True(json.TryGetProperty("email", out _));
        Assert.Matches(new Regex("^/uploads/avatars/"), json.GetProperty("avatarUrl").GetString()!);
    }
}
