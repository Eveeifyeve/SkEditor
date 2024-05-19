﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SkEditor.API;

namespace SkEditor.Utilities.Docs.SkUnity;

public class SkUnityProvider : IDocProvider
{
    private const string BaseUri = "https://api.skunity.com/v1/%s/docs/search/";
    private const string ExampleUri = "https://api.skunity.com/v1/%s/docs/getExamplesByID/";

    private readonly HttpClient _client = new HttpClient()
        .WithUserAgent("C# App");
    
    public DocProvider Provider => DocProvider.SkUnity;
    public List<string> CanSearch(SearchData searchData)
    {
        if (searchData.Query.Length < 3 && string.IsNullOrEmpty(searchData.FilteredAddon) && searchData.FilteredType == IDocumentationEntry.Type.All)
            return ["Query must be at least 3 characters long"];
        
        return [];
    }

    public Task<IDocumentationEntry> FetchElement(string id)
    {
        throw new NotImplementedException();
    }

    public async Task<List<IDocumentationEntry>> Search(SearchData searchData)
    {
        // First build the URI
        var uri = BaseUri.Replace("%s", ApiVault.Get().GetAppConfig().SkUnityAPIKey);
        var queryElements = new List<string>();

        if (!string.IsNullOrEmpty(searchData.Query))
            queryElements.Add(searchData.Query);
        if (searchData.FilteredType != IDocumentationEntry.Type.All)
            queryElements.Add("type:" + searchData.FilteredType.ToString().ToLower() + "s");
        if (!string.IsNullOrEmpty(searchData.FilteredAddon))
            queryElements.Add("addon:" + searchData.FilteredAddon);

        uri += string.Join("%20", queryElements);
        
        var cancellationToken = new CancellationTokenSource(new TimeSpan(0, 0, 5));
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(uri, cancellationToken.Token);
        }
        catch (Exception e)
        {
            ApiVault.Get().ShowError(e is TaskCanceledException
                ? "The request to the documentation server timed out. Are the docs down?"
                : $"An error occurred while fetching the documentation.\n\n{e.Message}");
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            ApiVault.Get().ShowError($"An error occurred while fetching the documentation.\n\nReceived status code: {response.StatusCode}");
            return new List<IDocumentationEntry>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken.Token);
        var responseObject = JObject.Parse(content);
        if (responseObject["response"].ToString() != "success")
        {
            ApiVault.Get().ShowError($"An error occurred while fetching the documentation.\n\nReceived response: {responseObject["response"]}");
            return new List<IDocumentationEntry>();
        }
        var entries = responseObject["result"].ToObject<List<SkUnityDocEntry>>();
        return entries.ToList<IDocumentationEntry>();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(ApiVault.Get().GetAppConfig().SkUnityAPIKey);
    }
    
    public static IDocProvider Get() => (SkUnityProvider) IDocProvider.Providers[DocProvider.SkUnity];

    public bool NeedsToLoadExamples => true;

    public async Task<List<IDocumentationExample>> FetchExamples(string elementId)
    {
        var uri = ExampleUri.Replace("%s", ApiVault.Get().GetAppConfig().SkUnityAPIKey) + elementId;
        
        var cancellationToken = new CancellationTokenSource(new TimeSpan(0, 0, 5));
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(uri, cancellationToken.Token);
        }
        catch (Exception e)
        {
            ApiVault.Get().ShowError(e is TaskCanceledException
                ? "The request to the documentation server timed out. Are the docs down?"
                : $"An error occurred while fetching the documentation.\n\n{e.Message}");
            return [];
        }
        
        if (!response.IsSuccessStatusCode)
        {
            ApiVault.Get().ShowError($"An error occurred while fetching the documentation.\n\nReceived status code: {response.StatusCode}");
            return new List<IDocumentationExample>();
        }
        
        var content = await response.Content.ReadAsStringAsync(cancellationToken.Token);
        var responseObject = JObject.Parse(content);
        var resultObject = responseObject["result"].ToObject<JObject>();

        var keys = new List<string>();
        foreach (var key in resultObject.Properties())
        {
            if (int.TryParse(key.Name, out _))
                keys.Add(key.Name);
        }
        
        return keys.Select(key => resultObject[key].ToObject<SkUnityDocExample>()).ToList<IDocumentationExample>();
    }
}