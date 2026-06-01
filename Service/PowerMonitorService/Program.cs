using PowerMonitorService.Hubs;
using PowerMonitorService.Services;
using Microsoft.EntityFrameworkCore;
using PowerMonitorService.Data;

var builder = WebApplication.CreateBuilder(args);

// 1. Configurar CORS para Angular (puerto 4200) con soporte para SignalR WebSockets
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Necesario para conexiones de SignalR
    });
});

// 2. Agregar soporte para controladores de API
builder.Services.AddControllers();

// Registrar DbContext de EF Core con SQL Server
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<CurrentMonitorDbContext>(options =>
    options.UseSqlServer(connectionString));

// 3. Agregar SignalR para WebSockets en tiempo real
builder.Services.AddSignalR();

// 4. Registrar SerialService como Singleton
builder.Services.AddSingleton<SerialService>();

// 5. Agregar soporte para OpenAPI (Swagger)
builder.Services.AddOpenApi();

var app = builder.Build();

// Configurar el pipeline de peticiones HTTP
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Activar la política de CORS antes de controladores y hubs
app.UseCors("CorsPolicy");

// Servir la aplicación Angular desde el backend en el puerto 5200
var angularPath = Path.Combine(app.Environment.ContentRootPath, "..", "..", "View", "PowerMonitorView", "dist", "PowerMonitorView", "browser");
if (Directory.Exists(angularPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(angularPath)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(angularPath)
    });
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(angularPath)
    });
}

app.UseAuthorization();

// Mapear los controladores API REST
app.MapControllers();

// Mapear el Hub de SignalR para el frontend
app.MapHub<SerialHub>("/hubs/serial");

// Forzar al backend a correr en http://0.0.0.0:5200
app.Run("http://0.0.0.0:5200");
