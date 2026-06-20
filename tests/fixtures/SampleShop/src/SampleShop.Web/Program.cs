var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));
app.MapPost("/hooks/stripe", () => Results.Ok());

app.Run();
