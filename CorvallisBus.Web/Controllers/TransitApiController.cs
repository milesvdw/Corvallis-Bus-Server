﻿using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Mvc;
using CorvallisBus.Core.DataAccess;
using CorvallisBus.Core.WebClients;
using CorvallisBus.Core.Models;
using Microsoft.AspNetCore.Hosting;
using System.Runtime.InteropServices;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace CorvallisBus.Controllers
{
    [Route("api")]
    public class TransitApiController : Controller
    {
        private static readonly string _destinationTimeZoneId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Pacific Standard Time"
                : "America/Los_Angeles";

        private readonly ITransitRepository _repository;
        private readonly ITransitClient _client;
        private readonly Func<DateTimeOffset> _getCurrentTime;

        public TransitApiController(IHostingEnvironment env)
        {
            _repository = new MemoryTransitRepository(env.WebRootPath);
            _client = new TransitClient();
            _getCurrentTime = () => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.Now, _destinationTimeZoneId);
        }

        /// <summary>
        /// Redirects the user to the GitHub repo where this API is documented.
        /// </summary>
        [HttpGet]
        public ActionResult Index()
        {
            return View();
        }

        [HttpGet("static")]
        public ActionResult GetStaticData()
        {
            return PhysicalFile(_repository.StaticDataPath, "application/json");
        }

        public List<int> ParseStopIds(string stopIds)
        {
            if (string.IsNullOrWhiteSpace(stopIds))
            {
                return new List<int>();
            }

            // ToList() this to force any parsing exception to happen here,
            // rather than later, because I'm lazy and don't wanna reason my way
            // through deferred execution and exception-handling.
            return stopIds.Split(',').Select(id => int.Parse(id)).ToList();
        }

        /// <summary>
        /// As the name suggests, this gets the ETA information for any number of stop IDs.  The data
        /// is represented as a dictionary, where the keys are the given stop IDs and the values are dictionaries.
        /// These nested dictionaries have route numbers as the keys and integers (ETA) as the values.
        /// </summary>
        [HttpGet("eta/{stopIds}")]
        public async Task<ActionResult> GetETAs(string stopIds)
        {
            List<int> parsedStopIds;
            try
            {
                parsedStopIds = ParseStopIds(stopIds);
            }
            catch (FormatException)
            {
                return StatusCode(400);
            }

            if (parsedStopIds == null || parsedStopIds.Count == 0)
            {
                return StatusCode(400);
            }

            try
            {
                var etas = await TransitManager.GetEtas(_repository, _client, parsedStopIds);
                var etasJson = JsonConvert.SerializeObject(etas);
                return Content(etasJson, "application/json");
            }
            catch
            {
                return StatusCode(500);
            }
        }

        /// <summary>
        /// Generates a new LatLong based on input.  Throws an exception if it can't do it.
        /// </summary>
        private static LatLong? ParseUserLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var locationPieces = location.Split(',');
            if (locationPieces.Length != 2)
            {
                throw new FormatException("2 comma-separated numbers must be provided in the location string.");
            }

            return new LatLong(double.Parse(locationPieces[0]),
                               double.Parse(locationPieces[1]));
        }

        /// <summary>
        /// Endpoint for the Corvallis Bus iOS app's favorites extension.
        /// </summary>
        [HttpGet("favorites")]
        public async Task<ActionResult> GetFavoritesViewModel(string location, string stops)
        {
            LatLong? userLocation;
            List<int> parsedStopIds;

            userLocation = ParseUserLocation(location);
            parsedStopIds = ParseStopIds(stops);

            if (userLocation == null && (parsedStopIds == null || parsedStopIds.Count == 0))
            {
                throw new ArgumentException($"One of {nameof(location)} or {nameof(stops)} must be non-empty.");
            }

            var viewModel = await TransitManager.GetFavoritesViewModel(_repository, _client, _getCurrentTime(), parsedStopIds, userLocation);
            var viewModelJson = JsonConvert.SerializeObject(viewModel);
            return Content(viewModelJson, "application/json");
        }

        /// <summary>
        /// Exposes the schedule that CTS routes adhere to for a set of stops.
        /// </summary>
        [HttpGet("schedule/{stopIds}")]
        public async Task<ActionResult> GetSchedule(string stopIds)
        {
            List<int> parsedStopIds;

            try
            {
                parsedStopIds = ParseStopIds(stopIds);
            }
            catch (FormatException)
            {
                return StatusCode(400);
            }

            if (parsedStopIds == null || parsedStopIds.Count == 0)
            {
                return StatusCode(400);
            }

            try
            {
                var todaySchedule = await TransitManager.GetSchedule(_repository, _client, _getCurrentTime(), parsedStopIds);
                var todayScheduleJson = JsonConvert.SerializeObject(todaySchedule);
                return Content(todayScheduleJson, "application/json");
            }
            catch
            {
                return StatusCode(500);
            }
        }

        [HttpGet("arrivals-summary/{stopIds}")]
        public async Task<ActionResult> GetArrivalsSummary(string stopIds)
        {
            List<int> parsedStopIds;

            try
            {
                parsedStopIds = ParseStopIds(stopIds);
            }
            catch (FormatException)
            {
                return StatusCode(400);
            }

            if (parsedStopIds == null || parsedStopIds.Count == 0)
            {
                return StatusCode(400);
            }

            try
            {
                var arrivalsSummary = await TransitManager.GetArrivalsSummary(_repository, _client, _getCurrentTime(), parsedStopIds);
                var arrivalsSummaryJson = JsonConvert.SerializeObject(arrivalsSummary);
                return Content(arrivalsSummaryJson, "application/json");
            }
            catch
            {
                return StatusCode(500);
            }
        }

        [HttpGet("service-alerts")]
        public Task<List<ServiceAlert>> GetServiceAlerts()
        {
            return _client.GetServiceAlerts();
        }

        [HttpGet("service-alerts/html")]
        public ActionResult GetServiceAlertsWebsite()
        {
            return Redirect("https://www.corvallisoregon.gov/news?field_microsite_tid=581");
        }

        /// <summary>
        /// Performs a first-time setup and import of static data.
        /// </summary>
        [HttpPost("job/init")]
        public ActionResult Init()
        {
            var expectedAuth = Environment.GetEnvironmentVariable("CorvallisBusAuthorization");
            if (!string.IsNullOrEmpty(expectedAuth))
            {
                string authValue = Request.Headers["Authorization"];
                if (expectedAuth != authValue)
                {
                    return Unauthorized();
                }
            }

            try
            {
                var errors = DataLoadJob();
                if (errors.Count != 0)
                {
                    var message = GetValidationErrorMessage(errors);
                    SendNotification("corvallisb.us init job had validation errors", message).Wait();
                    return Ok(message);
                }

                return Ok("Init job successful.");
            }
            catch (Exception ex)
            {
                SendExceptionNotification(ex).Wait();
                throw;
            }
        }

        private Task SendExceptionNotification(Exception ex)
        {
            var lastWriteTime = System.IO.File.GetLastWriteTime(_repository.StaticDataPath);
            var htmlContent =
$@"<h2>Init job failed: {ex.Message}</h2>
<pre>{ex.StackTrace}</pre>

<p>Init files last updated on {lastWriteTime}</p>";
            return SendNotification(subject: "corvallisb.us init task threw an exception", htmlContent);
        }

        private string GetValidationErrorMessage(List<string> errors)
        {
            var lastWriteTime = System.IO.File.GetLastWriteTime(_repository.StaticDataPath);
            return $@"<h2>Init job had {errors.Count} validation errors</h2>
<pre>{string.Join('\n', errors)}</pre>

<p>Init files last updated on {lastWriteTime}</p>";
        }


        private async Task SendNotification(string subject, string htmlContent)
        {
            var apiKey = Environment.GetEnvironmentVariable("CorvallisBusSendGridKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                return;
            }

            var client = new SendGridClient(apiKey);
            var from = new EmailAddress("azure_78fe1edda7f051b6116e50e4617e08f1@azure.com", "corvallisb.us Notification");
            var to = new EmailAddress("rikkigibson@gmail.com", "Rikki Gibson");

            var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent: subject, htmlContent);
            _ = await client.SendEmailAsync(msg);
        }

        private List<string> DataLoadJob()
        {
            var (busSystemData, errors) = _client.LoadTransitData();

            _repository.SetStaticData(busSystemData.StaticData);
            _repository.SetPlatformTags(busSystemData.PlatformIdToPlatformTag);
            _repository.SetSchedule(busSystemData.Schedule);

            return errors;
        }
    }
}
