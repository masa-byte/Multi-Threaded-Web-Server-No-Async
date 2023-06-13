using Newtonsoft.Json;
using Project1;
using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

//
string listeningPort = "http://localhost:5000/";
string baseUrl = "https://api.spotify.com/v1/search";

// shared resources
LimitedCache<string, Response> cache = new LimitedCache<string, Response>();
int numberOfRequest = 0;
object consoleLocker = new object();
object cacheLocker = new object();
SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

// for spotify api
string clientId = "";   // your client Id
string clientSecret = "";  // your client secret
SpotifyToken token = new SpotifyToken();
DateTime? expirationTime = null;


StartServer();


#region server
void StartServer()
{
    Thread serverThread = new Thread(() =>
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(listeningPort);
        listener.Start();
        Console.WriteLine($"Server is listening on {listener.Prefixes.Last()}");

        while (true)
        {
            var context = listener.GetContext();

            ThreadPool.QueueUserWorkItem(state =>
            {
                var request = ((HttpListenerContext)state).Request;
                var response = ((HttpListenerContext)state).Response;
                Response res = new Response();
                int myRequestNumber = 0;
                bool inCache = false;

                lock (consoleLocker)
                {
                    myRequestNumber = ++numberOfRequest;
                    LogRequest(request, myRequestNumber);
                }

                if (request.HttpMethod != "GET") 
                {
                    string badRequest = "This is not a valid request! Only GET methods are allowed";
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(badRequest);

                    res.Text = badRequest;
                    res.ByteBuffer = buffer;

                    SendResponse(response, res);
                }
                else
                {
                    if (Monitor.TryEnter(cacheLocker, 300))
                    {
                        inCache = cache.TryGetValue(request.RawUrl, out res);
                        Monitor.Exit(cacheLocker);
                    }

                    if (inCache == true)
                    {
                        SendResponse(response, res);

                        lock (consoleLocker)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"Cache hit for request {myRequestNumber}");
                            Console.ResetColor();
                        }
                    }
                    else
                    {
                        res = new Response();
                        string url = ParseQueryString(request);

                        if (url.Contains("Error") == false)
                        {
                            if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).TotalSeconds >= 3560)
                            {
                                semaphoreSlim.Wait();
                                if (expirationTime == null || DateTime.Now.Subtract(expirationTime.Value).TotalSeconds >= 3560)
                                    GetAccessToken();
                                semaphoreSlim.Release();
                            }

                            MakeSpotifyAPIRequest(url, res);

                            if (Monitor.TryEnter(cacheLocker))
                            {
                                cache.Add(request.RawUrl, res);
                                Monitor.Exit(cacheLocker);
                            }
                        }
                        else
                        {
                            string badRequest = "This is not a valid request! ";
                            if (url.Contains("Q"))
                                badRequest += "Q parameter is missing";
                            else if (url.Contains("Type"))
                                badRequest += "Type parameter is missing or wrong. Only artist and track are allowed";
                            else if (url.Contains("Both"))
                                badRequest += "Both Q and Type parameters are missing";

                            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(badRequest);

                            res.Text = badRequest;
                            res.ByteBuffer = buffer;
                        }

                        SendResponse(response, res);
                    }         
                }

                lock (consoleLocker)
                {
                    LogResponse(response, myRequestNumber);
                }

            }, context);
        }

        listener.Close();
    });

    serverThread.Start();
    serverThread.Join();
}
#endregion


#region functions

void GetAccessToken()
{
    var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
    var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
    request.Headers.Add("Authorization", $"Basic {authHeader}");
    request.Content = new FormUrlEncodedContent(new[]
    {
            new KeyValuePair<string, string>("grant_type", "client_credentials")
    });
    var client = new HttpClient();
    var response = client.SendAsync(request).Result;
    var responseContent = response.Content.ReadAsStringAsync().Result;
    token = JsonConvert.DeserializeObject<SpotifyToken>(responseContent);
    expirationTime = DateTime.Now;

    lock (consoleLocker)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Spotify token refreshed");
        Console.ResetColor();
    }
}

void LogRequest(HttpListenerRequest request, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(request.HttpMethod);
    Console.WriteLine(request.ProtocolVersion);
    Console.WriteLine(request.Url);
    Console.WriteLine(request.RawUrl);
    Console.WriteLine(request.Headers);
}

void LogResponse(HttpListenerResponse response, int requestNumber)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Logging response for request {requestNumber}");
    Console.ResetColor();
    Console.WriteLine(response.StatusCode);
    Console.WriteLine(response.StatusDescription);
    Console.WriteLine(response.ProtocolVersion);
    Console.WriteLine(response.Headers);
}

string ParseQueryString(HttpListenerRequest request)
{
    string url = baseUrl + "?";
    bool qFound = false;
    bool typeFound = false;

    for (int i = 0; i < request.QueryString.Count; i++)
    {
        var key = request.QueryString.GetKey(i);
        var values = request.QueryString.GetValues(key);

        if (key == "q")
        {
            url += key + "=" + Uri.EscapeDataString(string.Join(", ", values));
            qFound = true;
        }
        else
        {
            url += "&" + key + "=" + string.Join(", ", values);
        }
        if (key == "type")
        {
            if (string.Join(", ", values) == "artist" || string.Join(", ", values) == "track")
                typeFound = true;
        }
    }
    if (qFound && typeFound)
        return url;
    if (qFound == false && typeFound == false)
        return "Error. Both";
    if (typeFound == false)
        return "Error. Type";
    else
        return "Error. Q";
}

void MakeSpotifyAPIRequest(string url, Response res)
{
    string responseBody;
    byte[] buffer;

    HttpClient client = new HttpClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
    var response = client.GetAsync(url).Result;
    responseBody = response.Content.ReadAsStringAsync().Result;

    client.Dispose();

    var doc = JsonDocument.Parse(responseBody);
    var root = doc.RootElement;


    if (root.TryGetProperty("tracks", out JsonElement tracks) && tracks.GetProperty("items").GetArrayLength() == 0)
    {
        responseBody = "There are no tracks or albums like this!";
    }
    if (root.TryGetProperty("artists", out JsonElement artists) && artists.GetProperty("items").GetArrayLength() == 0)
    {
        responseBody = "There are no tracks or albums like this!";
    }

    buffer = System.Text.Encoding.UTF8.GetBytes(responseBody);

    res.Text = responseBody;
    res.ByteBuffer = buffer;
}

void SendResponse(HttpListenerResponse response, Response res)
{
    response.ContentLength64 = res.ByteBuffer.Length;
    var output = response.OutputStream;
    output.Write(res.ByteBuffer, 0, res.ByteBuffer.Length);
    output.Close();
}
#endregion


#region classes
class Response
{
    public string? Text;
    public byte[]? ByteBuffer;
};

class SpotifyToken
{
    public string? access_token { get; set; }
    public string? token_type { get; set; }
    public int expires_in { get; set; }
}
#endregion
