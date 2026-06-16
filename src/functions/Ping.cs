using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Playground.Functions;

public class Ping
{
    [Function("Ping")]
    public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData req)
    {
        var r = req.CreateResponse(HttpStatusCode.OK);
        r.WriteString("pong");
        return r;
    }
}
