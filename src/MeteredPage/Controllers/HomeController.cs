﻿using System;
using LandingPage.ViewModels.Home;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using Microsoft.Marketplace.SaaS;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using MeteredPage.ViewModels;
using Newtonsoft.Json;
using LandingPage.Services;
using LandingPage.Entities;
using System.Linq;
namespace LandingPage.Controllers
{
    [Authorize(AuthenticationSchemes = OpenIdConnectDefaults.AuthenticationScheme)]
    [AuthorizeForScopes(Scopes = new string[] { "user.read" })]
    public class HomeController : Controller
    {
        private readonly IMarketplaceSaaSClient _marketplaceSaaSClient;
        private readonly GraphServiceClient _graphServiceClient;
        private IConfiguration _configuration;

        private string subscriptionKey = "";
        private string ocrEndPoint = "";
        private readonly ICosmosDbService _cosmosDbService;

        public HomeController(
            IMarketplaceSaaSClient marketplaceSaaSClient,
            GraphServiceClient graphServiceClient,
            ICosmosDbService cosmosDbService,
            IConfiguration Configuration)
        {
            _marketplaceSaaSClient = marketplaceSaaSClient;
            _graphServiceClient = graphServiceClient;
            _configuration = Configuration;
            _cosmosDbService = cosmosDbService;
        }

        /// <summary>
        /// Shows all information associated with the user, the request, and the subscription.
        /// </summary>
        /// <param name="token">THe marketplace purchase ID token</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns></returns>
        public async Task<IActionResult> IndexAsync(string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(token))
            {
                this.ModelState.AddModelError(string.Empty, "Token URL parameter cannot be empty");
                this.ViewBag.Message = "Token URL parameter cannot be empty";
                return this.View();
            }

            token = token.Replace(" ", "+");
            // resolve the subscription using the marketplace purchase id token
            var resolvedSubscription = (await _marketplaceSaaSClient.Fulfillment.ResolveAsync(token, cancellationToken: cancellationToken)).Value;
            HttpContext.Session.SetString("currentSubscriptionId", resolvedSubscription.Subscription.Id.ToString());
            // get the plans on this subscription
            var subscriptionPlans = (await _marketplaceSaaSClient.Fulfillment.ListAvailablePlansAsync(resolvedSubscription.Id.Value, cancellationToken: cancellationToken)).Value;
            
            // find the plan that goes with this purchase
            string planName = string.Empty;
            foreach (var plan in subscriptionPlans.Plans)
            {
                if (plan.PlanId == resolvedSubscription.Subscription.PlanId)
                {
                    planName = plan.DisplayName;
                    HttpContext.Session.SetString("currentPlanId", plan.PlanId);

                    // get Demension
                    // This Demo Support only one Meter Dimension
                   foreach (var component in plan.PlanComponents.MeteringDimensions)
                    {
                        HttpContext.Session.SetString("currentDimensionId", component.Id);
                        break;
                    }
                }
            }

            // get graph current user data
            var graphApiUser = await _graphServiceClient.Me.Request().GetAsync();
           
            // build the model
            var model = new IndexViewModel
            {
                DisplayName = graphApiUser.DisplayName,
                Email = graphApiUser.Mail,
                SubscriptionName = resolvedSubscription.SubscriptionName,
                FulfillmentStatus = resolvedSubscription.Subscription.SaasSubscriptionStatus.GetValueOrDefault(),
                PlanName = planName,
                SubscriptionId = resolvedSubscription.Id.ToString(),
                TenantId = resolvedSubscription.Subscription.Beneficiary.TenantId.ToString(),
                PurchaseIdToken = token
            };

            
            
            return View(model);
        }

        [Route("Details")]
        public async Task<IActionResult> DetailsAsync(string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(token))
            {
                this.ModelState.AddModelError(string.Empty, "Token URL parameter cannot be empty");
                this.ViewBag.Message = "Token URL parameter cannot be empty";
                return this.View();
            }

            // resolve the subscription using the marketplace purchase id token
            var resolvedSubscription = (await _marketplaceSaaSClient.Fulfillment.ResolveAsync(token, cancellationToken: cancellationToken)).Value;
            var subscriptionPlans = (await _marketplaceSaaSClient.Fulfillment.ListAvailablePlansAsync(resolvedSubscription.Id.Value, cancellationToken: cancellationToken)).Value;

            // get graph current user data
            var graphApiUser = await _graphServiceClient.Me.Request().GetAsync();

            // build the model
            var model = new DetailsViewModel()
            {
                PurchaseIdToken = token,
                UserClaims = this.User.Claims,
                GraphUser = graphApiUser,
                Subscription = resolvedSubscription.Subscription,
                SubscriptionPlans = subscriptionPlans
            };

            return View(model);
        }


        /// <summary>
        /// OCR Processing this instance.
        /// </summary>
        /// <returns> OCR instance.</returns>
        [HttpPost]
        public async Task<IActionResult> IndexAsync(List<IFormFile> files)
        {
            IndexViewModel view = new IndexViewModel();
            view.FulfillmentStatus = Microsoft.Marketplace.SaaS.Models.SubscriptionStatusEnum.Subscribed;

            var filePaths = new List<string>();
            foreach (var formFile in files)
            {
                if (formFile.Length > 0)
                {
                    // full path to file in temp location
                    using (var ms = new MemoryStream())
                    {
                        formFile.CopyTo(ms);
                        var fileBytes = ms.ToArray();
                        view.OcrDetail = await MakeOCRRequest(fileBytes);
                        
                        // act on the Base64 data
                    }
                }
            }
            return this.View(view);

        }


        /// <summary>
        /// Gets the text visible in the specified image file by using
        /// the Computer Vision REST API.
        /// </summary>
        /// <param name="imageFilePath">The image file with printed text.</param>
        public async Task<string> MakeOCRRequest(byte[] byteData)
        {
            this.subscriptionKey = _configuration["subscriptionKey"];
            this.ocrEndPoint = _configuration["ocrEndPoint"];
            if (!String.IsNullOrEmpty(this.subscriptionKey)
                && !String.IsNullOrEmpty(this.ocrEndPoint))
            {
                try
                {
                    HttpClient client = new HttpClient();
                    string uriBase = this.ocrEndPoint + "vision/v2.1/ocr";
                    // Request headers.
                    client.DefaultRequestHeaders.Add(
                        "Ocp-Apim-Subscription-Key", this.subscriptionKey);

                    string requestParameters = "language=unk&detectOrientation=true";

                    // Assemble the URI for the REST API method.
                    string uri = uriBase + "?" + requestParameters;

                    HttpResponseMessage response;

                    // Add the byte array as an octet stream to the request body.
                    using (ByteArrayContent content = new ByteArrayContent(byteData))
                    {
                        // This example uses the "application/octet-stream" content type.
                        // The other content types you can use are "application/json"
                        // and "multipart/form-data".
                        content.Headers.ContentType =
                            new MediaTypeHeaderValue("application/octet-stream");

                        // Asynchronously call the REST API method.
                        response = await client.PostAsync(uri, content);
                    }

                    // Asynchronously get the JSON response.
                    string contentString = await response.Content.ReadAsStringAsync();

                    // Display the JSON response.
                    string result=JToken.Parse(contentString).ToString();

                    AzureOcrModel ocrResult = JsonConvert.DeserializeObject<AzureOcrModel>(result);

                    List<string> ocrText = ocrResult.regions.SelectMany(r => r.lines.SelectMany(l => l.words).Select(w => w.text)).ToList();
                    //CallMeteredAudit(ocrText);

                    string finalText = ocrText.Aggregate("", (current, s) => current + (s + ","));
                    
                    // Take JSON and Return the words only. 
                    // Need to Mapp JSON to Model Entities then use LINQ
                    CallMeteredAudit(finalText, ocrText.Count);

                    return finalText;
                }
                catch (Exception e)
                {
                    return e.Message;
                }
            }
            else
            {
                return "Missing OCR Configuration Information";
            }
        }



        /// <summary>
        /// Call Azure CosmosDB to Record the Metered Transaction
        /// </summary>
        /// <param name="ocrText">ocr data</param>
        /// <param name="ocrTextCount">ocr word count</param>
        public async void CallMeteredAudit(string ocrText, int ocrTextCount)
        {

            if (!String.IsNullOrEmpty(HttpContext.Session.GetString("currentSubscriptionId"))
                && !String.IsNullOrEmpty(HttpContext.Session.GetString("currentPlanId"))
                && !String.IsNullOrEmpty(HttpContext.Session.GetString("currentDimensionId"))
                )
            {

                try
                {

                    Item item = new Item();
                    // Add to DB direct using Repository Pattern
                    item.id = Guid.NewGuid().ToString();
                    item.DimensionId = HttpContext.Session.GetString("currentDimensionId");
                    item.SubscriptionId = HttpContext.Session.GetString("currentSubscriptionId");
                    item.PlanId = HttpContext.Session.GetString("currentPlanId");
                    item.OcrData = ocrText;
                    item.OcrDataCount = ocrTextCount;
                    item.MeterProcessStatus = false;
                    item.CreatedDate = DateTime.Now;
                    await _cosmosDbService.AddAsync(item);

                }
                catch (Exception)
                {
                    throw;
                }
            }
        }
    }
}