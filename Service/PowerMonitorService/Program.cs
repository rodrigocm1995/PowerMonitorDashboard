using PowerMonitorService.Hubs;
using PowerMonitorService.Services;

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

app.UseAuthorization();

// Mapear los controladores API REST
app.MapControllers();

// Mapear el Hub de SignalR para el frontend
app.MapHub<SerialHub>("/hubs/serial");

// Forzar al backend a correr en http://localhost:5200
app.Run("http://localhost:5200");
