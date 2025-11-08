using SDSChat.Components;
using SDSChat.Services;

namespace SDSChat;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Add services
        builder.Services.AddScoped<IDocumentService, DocumentService>();
        builder.Services.AddScoped<ITextExtractionService, TextExtractionService>();
        builder.Services.AddScoped<IChatService, ChatService>();

        // Add API controllers
        builder.Services.AddControllers();

        // Configure HttpClient for Blazor Server
        builder.Services.AddHttpClient();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpClient>(sp =>
        {
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            var httpContext = httpContextAccessor.HttpContext;
            
            if (httpContext != null)
            {
                var request = httpContext.Request;
                var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
                return new HttpClient
                {
                    BaseAddress = new Uri(baseUrl)
                };
            }
            
            // Fallback - this shouldn't happen in normal Blazor Server scenarios
            return new HttpClient();
        });

        var app = builder.Build();

        // Ensure storage directory exists
        var storagePath = app.Configuration.GetValue<string>("DocumentSettings:StoragePath")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "Documents");
        if (!Directory.Exists(storagePath))
        {
            Directory.CreateDirectory(storagePath);
        }

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapControllers();

        app.Run();
    }
}