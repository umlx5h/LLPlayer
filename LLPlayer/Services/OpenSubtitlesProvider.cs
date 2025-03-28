using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Xml.Serialization;
using FlyleafLib;
using LLPlayer.Extensions;

namespace LLPlayer.Services;

public class OpenSubtitlesProvider
{
    private readonly FlyleafManager FL;
    private readonly HttpClient _client;
    private string? _token;
    private bool _initialized = false;

    public OpenSubtitlesProvider(FlyleafManager fl)
    {
        FL = fl;
        HttpClient client = new();
        client.BaseAddress = new Uri("http://api.opensubtitles.org/xml-rpc");
        client.Timeout = TimeSpan.FromSeconds(15);

        _client = client;
    }

    private readonly SemaphoreSlim _loginSemaphore = new(1);

    private async Task Initialize()
    {
        if (!_initialized)
        {
            try
            {
                await _loginSemaphore.WaitAsync();
                await Login();
                _initialized = true;
            }
            finally
            {
                _loginSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Login
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    private async Task Login()
    {
        string loginReqXml =
"""
<?xml version="1.0"?>
<methodCall>
  <methodName>LogIn</methodName>
  <params>
    <param>
      <value>
        <string/>
      </value>
    </param>
    <param>
      <value>
        <string/>
      </value>
    </param>
    <param>
      <value>en</value>
    </param>
    <param>
      <value>VLsub 0.10.2</value>
    </param>
  </params>
</methodCall>
""";

        var result = await _client.PostAsync(string.Empty, new StringContent(loginReqXml));
        var content = await result.Content.ReadAsStringAsync();

        var serializer = new XmlSerializer(typeof(MethodResponse));
        LoginResponse loginResponse = new();
        using (var reader = new StringReader(content))
        {
            MethodResponse? response = null;
            try
            {
                result.EnsureSuccessStatusCode();
                response = serializer.Deserialize(reader) as MethodResponse;
                if (response == null)
                {
                    throw new InvalidOperationException("response is not MethodResponse");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Can't parse the login content: {ex.Message}", ex)
                {
                    Data =
                    {
                        ["status_code"] = ((int)result.StatusCode).ToString(),
                        ["login_content"] = content
                    }
                };
            }

            foreach (var member in response.Params.Param.Value.Struct.Member)
            {
                var propertyName = member.Name.ToUpperFirstChar();
                switch (propertyName)
                {
                    case nameof(loginResponse.Token):
                        loginResponse.Token = member.Value.String;
                        break;
                    case nameof(loginResponse.Status):
                        loginResponse.Status = member.Value.String;
                        break;
                }
            }
        }

        if (loginResponse.StatusCode != "200")
            throw new InvalidOperationException($"Can't login because status is '{loginResponse.StatusCode}'");

        if (string.IsNullOrWhiteSpace(loginResponse.Token))
            throw new InvalidOperationException("Can't login because token is empty");

        _token = loginResponse.Token;
    }


    /// <summary>
    /// Search
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    public async Task<IList<SearchResponse>> Search(string query)
    {
        await Initialize();

        if (_token == null)
            throw new InvalidOperationException("token is not initialized");

        var subLanguageId = "all";
        var limit = 500;

        string searchReqXml =
$"""
<?xml version="1.0"?>
<methodCall>
  <methodName>SearchSubtitles</methodName>
  <params>
    <param>
      <value>{_token}</value>
    </param>
    <param>
      <value>
        <array>
          <data>
            <value>
              <struct>
                <member>
                  <name>query</name>
                  <value>{query}</value>
                </member>
                <member>
                  <name>sublanguageid</name>
                  <value>{subLanguageId}</value>
                </member>
              </struct>
            </value>
          </data>
        </array>
      </value>
    </param>
    <param>
      <value>
        <struct>
          <member>
            <name>limit</name>
            <value>
              <i4>{limit}</i4>
            </value>
          </member>
        </struct>
      </value>
    </param>
  </params>
</methodCall>
""";

        var result = await _client.PostAsync(string.Empty, new StringContent(searchReqXml));
        var content = await result.Content.ReadAsStringAsync();

        var serializer = new XmlSerializer(typeof(MethodResponse));

        List<SearchResponse> searchResponses = new();

        using (StringReader reader = new(content))
        {
            MethodResponse? response = null;
            try
            {
                result.EnsureSuccessStatusCode();
                response = serializer.Deserialize(reader) as MethodResponse;
                if (response == null)
                {
                    throw new InvalidOperationException("response is not MethodResponse");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Can't parse the search content: {ex.Message}", ex)
                {
                    Data =
                    {
                        ["status_code"] = ((int)result.StatusCode).ToString(),
                        ["search_content"] = content
                    }
                };
            }

            if (!response.Params.Param.Value.Struct.Member.Any(m => m.Name == "status" && m.Value.String == "200 OK"))
                throw new InvalidOperationException("Can't get the search result, status is not 200.");

            var resultMembers = response.Params.Param.Value.Struct.Member.FirstOrDefault(m => m.Name == "data");
            if (resultMembers == null)
                throw new InvalidOperationException("Can't get the search result, data is not found.");

            foreach (var record in resultMembers.Value.Array.Data.Value)
            {
                SearchResponse searchResponse = new();
                foreach (var member in record.Struct.Member)
                {
                    var propertyName = member.Name.ToUpperFirstChar();

                    var property = typeof(SearchResponse).GetProperty(propertyName);
                    if (property != null && property.CanWrite)
                    {
                        switch (Type.GetTypeCode(property.PropertyType))
                        {
                            case TypeCode.Int32:
                                if (member.Value.String != null &&
                                    int.TryParse(member.Value.String, out var n))
                                {
                                    property.SetValue(searchResponse, n);
                                }
                                else
                                {
                                    property.SetValue(searchResponse, member.Value.Int);
                                }
                                break;

                            case TypeCode.Double:
                                if (member.Value.String != null &&
                                    double.TryParse(member.Value.String, out var d))
                                {
                                    property.SetValue(searchResponse, d);
                                }
                                else
                                {
                                    property.SetValue(searchResponse, member.Value.Double);
                                }
                                break;

                            case TypeCode.String:
                                property.SetValue(searchResponse, member.Value.String);
                                break;
                        }
                    }
                }

                searchResponses.Add(searchResponse);
            }
        }

        return searchResponses;
    }

    /// <summary>
    /// Download
    /// </summary>
    /// <param name="sub"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    public async Task<(byte[] data, bool isBitmap)> Download(SearchResponse sub)
    {
        await Initialize();

        if (_token == null)
            throw new InvalidOperationException("token is not initialized");

        var idSubtitleFile = sub.IDSubtitleFile;
        var isBitmap = sub.SubFormat.ToLower() == "sub";

        string downloadReqXml =
$"""
 <?xml version="1.0"?>
 <methodCall>
   <methodName>DownloadSubtitles</methodName>
   <params>
     <param>
       <value>{_token}</value>
     </param>
     <param>
       <value>
         <array>
           <data>
             <value>{idSubtitleFile}</value>
           </data>
         </array>
       </value>
     </param>
   </params>
 </methodCall>
 """;

        var result = await _client.PostAsync(string.Empty, new StringContent(downloadReqXml));
        var content = await result.Content.ReadAsStringAsync();

        XmlSerializer serializer = new(typeof(MethodResponse));

        DownloadResponse downloadResponse = new();

        using (StringReader reader = new(content))
        {
            MethodResponse? response = null;
            try
            {
                result.EnsureSuccessStatusCode();
                response = serializer.Deserialize(reader) as MethodResponse;
                if (response == null)
                {
                    throw new InvalidOperationException("response is not MethodResponse");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Can't parse the download content: {ex.Message}", ex)
                {
                    Data =
                    {
                        ["status_code"] = ((int)result.StatusCode).ToString(),
                        ["download_content"] = content
                    }
                };
            }

            if (!response.Params.Param.Value.Struct.Member.Any(m => m.Name == "status" && m.Value.String == "200 OK"))
                throw new InvalidOperationException("Can't get the download result, status is not 200.");

            var resultMembers = response.Params.Param.Value.Struct.Member.FirstOrDefault(m => m.Name == "data");
            if (resultMembers == null)
                throw new InvalidOperationException("Can't get the download result, data is not found.");

            var data = resultMembers.Value.Array.Data.Value.First();

            foreach (var member in data.Struct.Member)
            {
                var propertyName = member.Name.ToUpperFirstChar();
                switch (propertyName)
                {
                    case nameof(downloadResponse.Data):
                        downloadResponse.Data = member.Value.String;
                        break;
                    case nameof(downloadResponse.Idsubtitlefile):
                        downloadResponse.Idsubtitlefile = member.Value.String;
                        break;
                }
            }
        }

        if (string.IsNullOrEmpty(downloadResponse.Data))
            throw new InvalidOperationException("Can't get the download result, base64 data is not found.");

        var gzipSub = Convert.FromBase64String(downloadResponse.Data);
        byte[]? subData;

        using MemoryStream compressedStream = new(gzipSub);
        using (GZipStream gzipStream = new(compressedStream, CompressionMode.Decompress))
        using (MemoryStream resultStream = new())
        {
            gzipStream.CopyTo(resultStream);
            subData = resultStream.ToArray();
        }

        if (subData == null)
            throw new InvalidOperationException("Can't get the download result, decompressed data is not found.");

        if (isBitmap)
        {
            return (subData, true);
        }

        Encoding? encoding = TextEncodings.DetectEncoding(subData);
        encoding ??= Encoding.UTF8;

        // If there is originally a BOM, it will remain even if it is converted to a string, so delete it.
        string subString = encoding.GetString(subData).TrimStart('\uFEFF');

        // Convert to UTF-8 and return
        byte[] subText = Encoding.UTF8.GetBytes(subString);

        if (FL.Config.Subs.SubsExportUTF8WithBom)
        {
            // append BOM
            subText = Encoding.UTF8.GetPreamble().Concat(subText).ToArray();
        }

        return (subText, false);
    }
}


public class LoginResponse
{
    public string Token { get; set; }
    public string Status { get; set; }
    public string StatusCode => Status.Split(' ')[0];
}

public class SearchResponse
{
    public string IDSubtitleFile { get; set; }
    public string SubFileName { get; set; }
    public int SubSize { get; set; } // from string
    public string SubLastTS { get; set; }
    public string IDSubtitle { get; set; }
    public string SubLanguageID { get; set; }
    public string SubFormat { get; set; }
    public string SubAddDate { get; set; }
    public double SubRating { get; set; } // from string
    public string SubSumVotes { get; set; }
    public int SubDownloadsCnt { get; set; } // from string
    public string MovieName { get; set; }
    public string MovieYear { get; set; }
    public string MovieKind { get; set; }
    public string ISO639 { get; set; }
    public string LanguageName { get; set; }
    public string SeriesSeason { get; set; }
    public string SeriesEpisode { get; set; }
    public string SubEncoding { get; set; }
    public string SubDownloadLink { get; set; }
    public string SubtitlesLink { get; set; }
    public double Score { get; set; }
}

public class DownloadResponse
{
    public string Data { get; set; }
    public string Idsubtitlefile { get; set; }
}


[XmlRoot("value")]
public class Value
{
    [XmlElement("string")]
    public string String { get; set; }

    [XmlElement("struct")]
    public Struct Struct { get; set; }

    [XmlElement("int")]
    public int Int { get; set; }

    [XmlElement("double")]
    public double Double { get; set; }

    [XmlElement("array")]
    public Array Array { get; set; }
}

[XmlRoot("member")]
public class Member
{

    [XmlElement("name")]
    public string Name { get; set; }

    [XmlElement("value")]
    public Value Value { get; set; }
}

[XmlRoot("struct")]
public class Struct
{

    [XmlElement("member")]
    public List<Member> Member { get; set; }
}

[XmlRoot("data")]
public class Data
{

    [XmlElement("value")]
    public List<Value> Value { get; set; }
}

[XmlRoot("array")]
public class Array
{

    [XmlElement("data")]
    public Data Data { get; set; }
}

[XmlRoot("param")]
public class Param
{
    [XmlElement("value")]
    public Value Value { get; set; }
}

[XmlRoot("params")]
public class Params
{

    [XmlElement("param")]
    public Param Param { get; set; }
}

[XmlRoot("methodResponse")]
public class MethodResponse
{

    [XmlElement("params")]
    public Params Params { get; set; }
}
