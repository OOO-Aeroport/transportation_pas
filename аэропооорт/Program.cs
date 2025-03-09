using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Указываем порт для текущего сервиса
builder.WebHost.UseUrls("http://*:5000");

var app = builder.Build();

// Конфигурация
var groundControlUrl = "http://ground-control:5001"; // URL сервиса диспетчера руления
var groundServiceUrl = "http://ground-service:5002"; // URL сервиса управления наземным обслуживанием
var passengerServiceUrl = "http://passenger-service:5003"; // URL сервиса пассажиров
var httpClient = new HttpClient();

// Эндпоинт для получения данных о пассажирах и багаже
app.MapPost("/transport_passengers", async context =>
{
    Console.WriteLine("Получен запрос на перевозку пассажиров и багажа");

    // Чтение данных из тела запроса
    var requestBody = await context.Request.ReadFromJsonAsync<TransportRequest>();
    if (requestBody == null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Неверный формат запроса");
        return;
    }

    // Обновление статуса пассажиров
    foreach (var passenger in requestBody.Passengers)
    {
        await UpdatePassengerStatusAsync(passenger);
    }

    // Получение маршрута от диспетчера руления
    var route = await GetRouteFromGroundControlAsync();

    // Перевозка пассажиров и багажа
    await TransportPassengersAsync(requestBody.Passengers, route);
    await TransportBaggageAsync(requestBody.Baggage, route);

    // Отправка отчета в управление наземным обслуживанием
    await SendReportToGroundServiceAsync("Перевозка завершена успешно");

    await context.Response.WriteAsync("Пассажиры и багаж успешно перевезены");
});

// Запуск приложения
app.Run();

// Метод для обновления статуса пассажира
async Task UpdatePassengerStatusAsync(Passenger passenger)
{
    try
    {
        var json = JsonSerializer.Serialize(passenger);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PutAsync($"{passengerServiceUrl}/api/passengers/{passenger.Id}", content);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"Статус пассажира {passenger.Id} обновлен на 'транспортировка'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при обновлении статуса пассажира: {ex.Message}");
    }
}

// Метод для получения маршрута от диспетчера руления
async Task<string> GetRouteFromGroundControlAsync()
{
    try
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/api/route");
        response.EnsureSuccessStatusCode();
        var route = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Получен маршрут: {route}");
        return route;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при получении маршрута: {ex.Message}");
        return string.Empty;
    }
}

// Метод для перевозки пассажиров
async Task TransportPassengersAsync(List<Passenger> passengers, string route)
{
    foreach (var passenger in passengers)
    {
        Console.WriteLine($"Пассажир {passenger.Id} перевозится по маршруту: {route}");
        await Task.Delay(100); // Имитация задержки перевозки
    }
}

// Метод для перевозки багажа
async Task TransportBaggageAsync(List<Baggage> baggage, string route)
{
    foreach (var bag in baggage)
    {
        Console.WriteLine($"Багаж {bag.Id} перевозится по маршруту: {route}");
        await Task.Delay(100); // Имитация задержки перевозки
    }
}

// Метод для отправки отчета в управление наземным обслуживанием
async Task SendReportToGroundServiceAsync(string report)
{
    try
    {
        var content = new StringContent(report, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"{groundServiceUrl}/api/report", content);
        response.EnsureSuccessStatusCode();
        Console.WriteLine("Отчет успешно отправлен в управление наземным обслуживанием");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при отправке отчета: {ex.Message}");
    }
}

// Модели данных
public class TransportRequest
{
    public List<Passenger> Passengers { get; set; }
    public List<Baggage> Baggage { get; set; }
}

public class Passenger
{
    public int Id { get; set; }
    public string Status { get; set; }
    public bool IsVip { get; set; }
    public int BaggageId { get; set; }
}

public class Baggage
{
    public int Id { get; set; }
    public int PassengerId { get; set; }
    public string Status { get; set; }
}