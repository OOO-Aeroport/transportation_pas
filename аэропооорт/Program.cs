using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://26.132.135.106:5555");

var app = builder.Build();

var httpClient = new HttpClient();

const string unoServiceUrl = "http://26.53.143.176:5555";
const string groundControlUrl = "http://26.21.3.228:5555";
const string passengerServiceUrl = "http://26.49.89.37:5555";
const string boardServiceUrl = "http://aircraft-service";

var passengerLoadQueue = new ConcurrentDictionary<int, List<int>>(); // For passenger loading (FlightId -> List<PassengerId>)
var passengerUnloadQueue = new ConcurrentDictionary<int, List<int>>(); // For passenger unloading
var baggageLoadQueue = new ConcurrentDictionary<int, List<BaggageInfo>>(); // For baggage loading
var baggageUnloadQueue = new ConcurrentDictionary<int, List<BaggageInfo>>(); // For baggage unloading

var activeOrders = new ConcurrentDictionary<string, int>(); // orderId -> FlightId
var waitingPassengers = new ConcurrentDictionary<int, List<int>>(); // Passengers waiting for their FlightId to match an order

var buses = new List<Vehicle> { new Vehicle("bus1"), new Vehicle("bus2"), new Vehicle("bus3") }; // 3 buses
var carts = new List<Vehicle> { new Vehicle("cart1"), new Vehicle("cart2"), new Vehicle("cart3") }; // 3 carts

// Добавим словарь для отслеживания состояния заказов
var orderStatus = new ConcurrentDictionary<string, OrderStatus>();

// Background task to fetch new passengers every 10 seconds
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(10000); // Wait for 10 seconds
        await FetchNewPassengersAsync();
    }
});


// Method to process fetched passengers
void ProcessPassengers(List<PassengerRequest> passengers)
{
    Console.WriteLine("Process started...");
    Console.WriteLine($"Number of passengers to process: {passengers.Count}");

    foreach (var passenger in passengers)
    {
        Console.WriteLine($"Processing passenger: ID = {passenger.passenger_id}, FlightId = {passenger.flight_id}");

        if (activeOrders.Values.Contains(passenger.flight_id))
        {
            Console.WriteLine($"FlightId {passenger.flight_id} matches an active order.");

            // Если FlightId совпадает с активным заказом, добавляем пассажира в очередь для отправки на борт
            var matchingOrder = activeOrders.FirstOrDefault(x => x.Value == passenger.flight_id);
            if (!string.IsNullOrEmpty(matchingOrder.Key))
            {
                Console.WriteLine($"Matching order found: OrderId = {matchingOrder.Key}, FlightId = {matchingOrder.Value}");

                // Добавляем в очередь для загрузки
                passengerLoadQueue.AddOrUpdate(
                    passenger.flight_id,
                    new List<int> { passenger.passenger_id },
                    (key, existingList) =>
                    {
                        Console.WriteLine($"Adding passenger {passenger.passenger_id} to the load queue for FlightId {passenger.flight_id}.");
                        existingList.Add(passenger.passenger_id);
                        return existingList;
                    });

                Console.WriteLine($"Passenger {passenger.passenger_id} added to the load queue.");

                // Записываем в JSON-файл
                Console.WriteLine("Attempting to write passenger to JSON file...");
                WritePassengerToFile(passenger, "matched_passengers.json");
                Console.WriteLine("Passenger written to JSON file.");
            }
            else
            {
                Console.WriteLine("No matching order found, even though FlightId matches active orders.");
            }
        }
        else
        {
            Console.WriteLine($"FlightId {passenger.flight_id} does not match any active orders.");

            // Если FlightId не совпадает, добавляем в список ожидания
            waitingPassengers.AddOrUpdate(
                passenger.flight_id,
                new List<int> { passenger.passenger_id },
                (key, existingList) =>
                {
                    Console.WriteLine($"Adding passenger {passenger.passenger_id} to the waiting list for FlightId {passenger.flight_id}.");
                    existingList.Add(passenger.passenger_id);
                    return existingList;
                });

            Console.WriteLine($"Passenger {passenger.passenger_id} added to the waiting list.");

            // Записываем в JSON-файл
            Console.WriteLine("Attempting to write passenger to JSON file...");
            WritePassengerToFile(passenger, "waiting_passengers.json");
            Console.WriteLine("Passenger written to JSON file.");
        }
    }

    Console.WriteLine("Process completed.");
}

// Method to fetch new passengers from the Passenger Service
async Task FetchNewPassengersAsync()
{
    try
    {
        // Получаем список пассажиров, которые соответствуют заказу от УНО
        var passengersToSend = GetPassengersForUnoOrder();

        // Сериализуем список пассажиров в JSON
        var jsonContent = new StringContent(JsonSerializer.Serialize(passengersToSend), Encoding.UTF8, "application/json");

        // Логируем тело запроса
        var requestBodyJson = await jsonContent.ReadAsStringAsync();
        Console.WriteLine($"Request body: {requestBodyJson}");

        // Отправляем POST-запрос
        var response = await httpClient.PostAsync($"{passengerServiceUrl}/passenger/transporting", jsonContent);

        // Логируем статус ответа
        Console.WriteLine($"Response status code: {response.StatusCode}");

        if (response.IsSuccessStatusCode)
        {
            // Логируем тело ответа
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response content: {responseContent}");

            // Десериализуем ответ в список пассажиров
            var passengers = await response.Content.ReadFromJsonAsync<List<PassengerRequest>>();
            if (passengers != null && passengers.Count > 0)
            {
                Console.WriteLine($"Fetched {passengers.Count} new passengers.");
                ProcessPassengers(passengers);
            }
            else
            {
                Console.WriteLine("No new passengers received.");
            }
        }
        else
        {
            Console.WriteLine($"Failed to fetch new passengers. Status code: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response content: {responseContent}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error fetching passengers: {ex.Message}");
    }
}

List<PassengerRequest> GetPassengersForUnoOrder()
{
    // Пример: получаем пассажиров, которые соответствуют активным заказам от УНО
    var passengers = new List<PassengerRequest>();

    foreach (var order in activeOrders)
    {
        if (passengerLoadQueue.TryGetValue(order.Value, out var passengerIds))
        {
            foreach (var passengerId in passengerIds)
            {
                passengers.Add(new PassengerRequest
                {
                    passenger_id = passengerId,
                    flight_id = order.Value
                });
            }
        }
    }

    return passengers;
}



// Method to write passengers to a JSON file
void WritePassengerToFile(PassengerRequest passenger, string fileName)
{
    var passengersList = new List<PassengerRequest>();
    if (File.Exists(fileName))
    {
        var json = File.ReadAllText(fileName);
        passengersList = JsonSerializer.Deserialize<List<PassengerRequest>>(json) ?? new List<PassengerRequest>();
    }

    passengersList.Add(passenger);
    var updatedJson = JsonSerializer.Serialize(passengersList, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(fileName, updatedJson);
}

// Исправленный метод для отправки пассажиров на борт
async Task SendPassengersToBoard(int flightId, List<int> passengerIds)
{
    try
    {
        // Создаем JSON с ID пассажиров
        var passengersRequest = new
        {
            FlightId = flightId,
            PassengerIds = passengerIds
        };

        var jsonData = JsonSerializer.Serialize(passengersRequest);
        var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        // Отправляем POST-запрос на борт
        var response = await httpClient.PostAsync($"{boardServiceUrl}/board_passengers/{flightId}", content);

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Пассажиры для рейса {flightId} успешно отправлены на борт.");
        }
        else
        {
            Console.WriteLine($"Ошибка при отправке пассажиров на борт: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Ответ сервера: {responseContent}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при отправке пассажиров на борт: {ex.Message}");
    }
}

// Accepting orders from UNO
app.MapGet("/plane-info/{flightId}/{orderId}", async (HttpContext context, int flightId, string orderId) =>
{
    // Инициализируем статус заказа
    orderStatus[orderId] = new OrderStatus();
    Console.WriteLine($"New order from UNO: flight {flightId}, orderId {orderId}");

    // Пример: симулируем завершение перевозки пассажиров и багажа
    await SimulateTransportCompletion(orderId, flightId);

    await context.Response.WriteAsync("Order accepted");
});

// Метод для симуляции завершения перевозки пассажиров и багажа
async Task SimulateTransportCompletion(string orderId, int flightId)
{
    // Симулируем завершение перевозки пассажиров
    await CompletePassengerTransport(orderId, flightId);

    // Симулируем завершение перевозки багажа
    await CompleteBaggageTransport(orderId, flightId);
}

// Метод для проверки завершения заказа и отправки отчета в УНО
async Task CheckAndReportOrderCompletion(string orderId)
{
    Console.WriteLine($"Checking order {orderId} for completion...");

    if (orderStatus.TryGetValue(orderId, out var status))
    {
        Console.WriteLine($"Order {orderId} found in orderStatus. PassengersDelivered: {status.PassengersDelivered}, BaggageDelivered: {status.BaggageDelivered}");

        // Проверяем, доставлены ли и пассажиры, и багаж
        if (status.PassengersDelivered && status.BaggageDelivered)
        {
            Console.WriteLine($"Both passengers and baggage for order {orderId} are delivered. Sending report to UNO...");

            // Отправляем отчет в УНО
            await ReportSuccessToUNO(orderId, "passenger-and-baggage-service");

            // Удаляем заказ из отслеживания
            orderStatus.TryRemove(orderId, out _);
            Console.WriteLine($"Order {orderId} completed and removed from orderStatus.");
        }
        else
        {
            Console.WriteLine($"Order {orderId} is not fully completed yet. PassengersDelivered: {status.PassengersDelivered}, BaggageDelivered: {status.BaggageDelivered}");
        }
    }
    else
    {
        Console.WriteLine($"Order {orderId} not found in orderStatus.");
    }
}

// Метод для обработки завершения перевозки пассажиров
async Task CompletePassengerTransport(string orderId, int flightId)
{
    Console.WriteLine($"Completing passenger transport for order {orderId}...");

    if (orderStatus.TryGetValue(orderId, out var status))
    {
        status.PassengersDelivered = true;
        Console.WriteLine($"Passengers for order {orderId} marked as delivered.");

        // Проверяем, можно ли отправить отчет
        await CheckAndReportOrderCompletion(orderId);
    }
    else
    {
        Console.WriteLine($"Order {orderId} not found in orderStatus. Cannot complete passenger transport.");
    }
}

// Метод для обработки завершения перевозки багажа
async Task CompleteBaggageTransport(string orderId, int flightId)
{
    Console.WriteLine($"Completing baggage transport for order {orderId}...");

    if (orderStatus.TryGetValue(orderId, out var status))
    {
        status.BaggageDelivered = true;
        Console.WriteLine($"Baggage for order {orderId} marked as delivered.");

        // Проверяем, можно ли отправить отчет
        await CheckAndReportOrderCompletion(orderId);
    }
    else
    {
        Console.WriteLine($"Order {orderId} not found in orderStatus. Cannot complete baggage transport.");
    }
}

// Метод для отправки отчета в УНО
async Task ReportSuccessToUNO(string orderId, string serviceName)
{
    try
    {
        // Формируем URL для отправки отчета
        var reportUrl = $"{unoServiceUrl}/uno/api/v1/order/successReport/{orderId}/{serviceName}";
        Console.WriteLine($"Preparing to send report to UNO. URL: {reportUrl}");

        Console.WriteLine($"Sending GET request to UNO...");
        // Отправляем GET-запрос
        var response = await httpClient.GetAsync(reportUrl);

        Console.WriteLine($"Success report sent to UNO for order {orderId}.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending success report to UNO: {ex.Message}");
    }
}

// Метод для транспортировки пассажиров (с учетом синхронизации)
async Task TransportPassengersAsync(int flightId, List<int> passengerIds, bool isLoad, string orderId)
{
    // Отмечаем завершение перевозки пассажиров
    await CompletePassengerTransport(orderId, flightId);

    //var bus = buses.FirstOrDefault(b => !b.IsBusy);
    //if (bus == null)
    //{
    //    Console.WriteLine("Нет доступных автобусов.");
    //    return;
    //}

    //bus.IsBusy = true;
    //try
    //{
    //    // Запрос на выезд из гаража
    //    await RequestGarageExit("bus");

    //    if (isLoad)
    //    {
    //        // Перевозка пассажиров от терминала к самолету
    //        await GetRouteToTerminal();
    //        Console.WriteLine($"Пассажиры для рейса {flightId} погружены в автобус.");

    //        await GetRouteToPlane(flightId);
    //        await SendPassengersToBoard(flightId, passengerIds);
    //        Console.WriteLine($"Пассажиры для рейса {flightId} доставлены к самолету.");
    //    }
    //    else
    //    {
    //        // Перевозка пассажиров от самолета к терминалу
    //        await GetRouteToPlane(flightId);
    //        Console.WriteLine($"Пассажиры для рейса {flightId} погружены в автобус.");

    //        await GetRouteToTerminal();
    //        Console.WriteLine($"Пассажиры для рейса {flightId} доставлены к терминалу.");
    //    }

    //    // Отмечаем завершение перевозки пассажиров
    //    await CompletePassengerTransport(orderId, flightId);
    //}
    //finally
    //{
    //    // Возврат в гараж
    //    await ReturnToGarage("bus");
    //    bus.IsBusy = false;
    //}
}

// Метод для транспортировки багажа (с учетом синхронизации)
async Task TransportBaggageAsync(int flightId, List<BaggageInfo> baggageList, bool isLoad, string orderId)
{
    // Отмечаем завершение перевозки багажа
    await CompleteBaggageTransport(orderId, flightId);

    //var cart = carts.FirstOrDefault(c => !c.IsBusy);
    //if (cart == null)
    //{
    //    Console.WriteLine("Нет доступных тележек.");
    //    return;
    //}

    //cart.IsBusy = true;
    //try
    //{
    //    // Запрос на выезд из гаража
    //    await RequestGarageExit("cart");

    //    if (isLoad)
    //    {
    //        // Перевозка багажа от грузового терминала к самолету
    //        await GetRouteToLuggageTerminal("1");
    //        Console.WriteLine($"Багаж для рейса {flightId} погружен в тележку.");

    //        await GetRouteToPlane(flightId);
    //        await SendBaggageToBoard(flightId, baggageList);
    //        Console.WriteLine($"Багаж для рейса {flightId} доставлен к самолету.");
    //    }
    //    else
    //    {
    //        // Перевозка багажа от самолета к грузовому терминалу
    //        await GetRouteToPlane(flightId);
    //        Console.WriteLine($"Багаж для рейса {flightId} погружен в тележку.");

    //        await GetRouteToLuggageTerminal("2");
    //        Console.WriteLine($"Багаж для рейса {flightId} доставлен к грузовому терминалу.");
    //    }

    //    // Отмечаем завершение перевозки багажа
    //    await CompleteBaggageTransport(orderId, flightId);
    //}
    //finally
    //{
    //    // Возврат в гараж
    //    await ReturnToGarage("cart");
    //    cart.IsBusy = false;
    //}
}

// Method to request garage exit
async Task RequestGarageExit(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/garage/{vehicleType}");
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Error exiting garage ({vehicleType})");
    }
}

// Method to get route to terminal
async Task<string> GetRouteToTerminal()
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/terminal1");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Failed to get route to terminal after 5 attempts");
    return null;
}

// Метод для получения маршрута до грузового терминала
async Task<string> GetRouteToLuggageTerminal(string currentPoint)
{
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/luggage");
        if (response.IsSuccessStatusCode)
        {
            var route = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Маршрут до грузового терминала получен.");
            return route;
        }
        await Task.Delay(1000);
    }
    Console.WriteLine("Не удалось получить маршрут до грузового терминала.");
    return null;
}

// Метод для отправки багажа на борт
async Task SendBaggageToBoard(int flightId, List<BaggageInfo> baggageList)
{
    try
    {
        // Создаем JSON с информацией о багаже
        var baggageRequest = new
        {
            FlightId = flightId,
            Baggage = baggageList
        };

        var jsonData = JsonSerializer.Serialize(baggageRequest);
        var content = new StringContent(jsonData, Encoding.UTF8, "application/json");

        // Отправляем POST-запрос на борт
        var response = await httpClient.PostAsync($"{boardServiceUrl}/bag/{flightId}", content);

        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Багаж для рейса {flightId} успешно отправлен на борт.");
        }
        else
        {
            Console.WriteLine($"Ошибка при отправке багажа на борт: {response.StatusCode}");
            var responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Ответ сервера: {responseContent}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка при отправке багажа на борт: {ex.Message}");
    }
}



// Method to get route to plane
async Task<string> GetRouteToPlane(int flightId)
{
    var currentPoint = "garage";
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var response = await httpClient.GetAsync($"{groundControlUrl}/{currentPoint}/plane/{flightId}");
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        await Task.Delay(1000);
    }
    Console.WriteLine($"Failed to get route to plane {flightId} after 5 attempts");
    return null;
}

// Method to return to garage
async Task ReturnToGarage(string vehicleType)
{
    var response = await httpClient.GetAsync($"{groundControlUrl}/plane/garage/{vehicleType}");
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Error returning to garage ({vehicleType})");
    }
}


// Receiving passenger data (updated)
app.MapPost("/transportation-pass", async (HttpContext context) =>
{
    var passengers = await context.Request.ReadFromJsonAsync<List<PassengerRequest>>();
    if (passengers == null || passengers.Count == 0)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid data");
        return;
    }

    // Process passengers
    ProcessPassengers(passengers);

    await context.Response.WriteAsync("Passengers processed");
});

// Starting the service
app.Run();

// Data models
public class PassengerRequest
{
    [JsonPropertyName("passenger_id")]
    public int passenger_id { get; set; }

    [JsonPropertyName("flight_id")]
    public int flight_id { get; set; }
}

public class PassengerIdsRequest
{
    public List<PassengerId> PassengerIds { get; set; }
}

public class PassengerId
{
    public int PassengerIds { get; set; }
}

public class BaggageData
{
    public int FlightID { get; set; }
    public int PassengerID { get; set; }
    public int BaggageWeight { get; set; }
}

public class BaggageInfo
{
    public int PassengerID { get; set; }
    public int Weight { get; set; }
}

public class Vehicle
{
    public string Id { get; set; }
    public bool IsBusy { get; set; }

    public Vehicle(string id)
    {
        Id = id;
        IsBusy = false;
    }
}

// Модель для отслеживания состояния заказа
public class OrderStatus
{
    public bool PassengersDelivered { get; set; } = false;
    public bool BaggageDelivered { get; set; } = false;
}