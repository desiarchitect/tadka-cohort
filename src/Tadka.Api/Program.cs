using Microsoft.EntityFrameworkCore;
using Tadka.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<TadkaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TadkaDb")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
