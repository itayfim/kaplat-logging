using System;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using log4net;
using log4net.Config;
using log4net.Appender;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using log4net.Core;

namespace kaplat_server
{
    class Program
    {
        private static HttpListener server;
        private static endPoint eEndPoint;
        private static HttpListenerContext context;
        private static Dictionary<string, Task> taskMap = new Dictionary<string, Task>();
        private static JObject result = new JObject();
        private static readonly ILog logger = LogManager.GetLogger(typeof(Program));
        private static int requestNumber = 1;
        private static Stopwatch stopwatch = new Stopwatch();
        private static bool firstRun = true;
        private static LogLevel requestDefLevel = LogLevel.INFO;
        private static LogLevel todoDefLevel = LogLevel.INFO;

        public static void Main()
        {
            startServer();
            server.BeginGetContext(new AsyncCallback(OnContextReceived), null);
            Console.ReadLine(); // wait for user input to exit
        }

        private static void OnContextReceived(IAsyncResult ar)
        {
            try
            {
                stopwatch.Start();
                context = server.EndGetContext(ar);
                context.Response.ContentType = "application/json";
                eEndPoint = getEndPoint(context);
                switch (eEndPoint)
                {
                    case endPoint.Health:
                        checkServerHealth();
                        break;
                    case endPoint.Create:
                        createTODO();
                        break;
                    case endPoint.Size:
                        getTODOsCount();
                        break;
                    case endPoint.Content:
                        getTODOsData();
                        break;
                    case endPoint.Update:
                        updateTODOStatus();
                        break;
                    case endPoint.Delete:
                        deleteTODO();
                        break;
                    case endPoint.GetLog:
                        getLogLevel();
                        break;
                    case endPoint.PutLog:
                        putLogLevel();
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        break;
                }

                logRequest();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                server.BeginGetContext(new AsyncCallback(OnContextReceived), null);
            }
        }

        private static void getLogLevel()
        {
            string loggerName = context.Request.QueryString["logger-name"];
            byte[] buffer;

            context.Response.StatusCode = 200;
            
            if (loggerName == "request-logger")
            {
                buffer = Encoding.UTF8.GetBytes(requestDefLevel.ToString());
            }
            else if (loggerName == "todo-logger")
            {
                buffer = Encoding.UTF8.GetBytes(todoDefLevel.ToString());
            }
            else
            {
                buffer = Encoding.UTF8.GetBytes("This is one line message to deliver the essence of GET failure");
            }

            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private static void putLogLevel()
        {
            string loggerName = context.Request.QueryString["logger-name"];
            string loggerLevel = context.Request.QueryString["logger-level"];
            LogLevel logLevel;
            byte[] buffer;

            context.Response.StatusCode = 200;
            if (!Enum.TryParse(loggerLevel, false, out logLevel))
            {
                buffer = Encoding.UTF8.GetBytes("This is one line message to deliver the essence of PUT failure");
                goto Write;
            }

            if (loggerName == "request-logger")
            {
                requestDefLevel = logLevel;
                buffer = Encoding.UTF8.GetBytes(requestDefLevel.ToString());
            }
            else if (loggerName == "todo-logger")
            {
                todoDefLevel = logLevel;
                buffer = Encoding.UTF8.GetBytes(todoDefLevel.ToString());
            }
            else
            {
                buffer = Encoding.UTF8.GetBytes("This is one line message to deliver the essence of the failure");
            }

            Write:
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private static void logRequest()
        {
            stopwatch.Stop();
            long duration = stopwatch.ElapsedMilliseconds;
            var resourceName = context.Request.Url.AbsolutePath;
            var httpVerb = context.Request.HttpMethod;
            LogRequest(LogLevel.INFO, $"Incoming request | #{requestNumber} | resource: {resourceName} | HTTP Verb {httpVerb}");
            if (requestDefLevel <= LogLevel.DEBUG)
            {
                LogRequest(LogLevel.DEBUG, $"request #{requestNumber} duration: {duration}ms");
            }
            requestNumber++;
        }

        private static void LogTODO(LogLevel level, string message)
        {
            string logLine = $"{DateTime.Now:dd-MM-yyyy hh:mm:ss.fff} {level}: {message} | request #{requestNumber}";

            Console.WriteLine(logLine);

            string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            string requestsLogFile = "todos.log";
            string logFilePath = Path.Combine(logsDirectory, requestsLogFile);
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine(logLine);
            }
        }

        private static void LogRequest(LogLevel level, string message)
        {
            string logLine = $"{DateTime.Now:dd-MM-yyyy hh:mm:ss.fff} {level}: {message} | request #{requestNumber}";

            Console.WriteLine(logLine); 

            string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            string requestsLogFile = "requests.log";
            string logFilePath = Path.Combine(logsDirectory, requestsLogFile);
            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine(logLine);
            }
        }

        private static void deleteTODO()
        {
            string idStr = context.Request.QueryString["id"];
            if (!idIsExist(idStr))
            {
                context.Response.StatusCode = 404;
                string errorMsg = $"Error: no such TODO with id {idStr}";
                createJsonResponse("errorMessage", errorMsg);

                LogTODO(LogLevel.ERROR, errorMsg);
            }
            else
            {
                context.Response.StatusCode = 200;
                int id = int.Parse(idStr);
                string title = "";
                foreach (KeyValuePair<string, Task> kvp in taskMap)
                {
                    if (kvp.Value.id == id)
                    {
                        title = kvp.Key;
                        break;
                    }
                }
                taskMap.Remove(title);
                createJsonResponse("result", taskMap.Count.ToString());

                LogTODO(LogLevel.INFO, $"Removing todo id {id}");
                if (todoDefLevel <= LogLevel.DEBUG)
                {
                    LogTODO(LogLevel.DEBUG, $"After removing todo id [{id}] there are {taskMap.Count} TODOs in the system");
                }
            }
        }

        private static void updateTODOStatus()
        {
            kaplat_server.Task.Status newStatus;
            bool error = false;
            string statusParam = context.Request.QueryString["status"];
            string idStr = context.Request.QueryString["id"];
            string oldStatus = "";
            string errorMsg = "";
            if (!idIsExist(idStr))
            {
                context.Response.StatusCode = 404;
                errorMsg = $"Error: no such TODO with id {idStr}";
                createJsonResponse("errorMessage", errorMsg);
                error = true;
            }
            else if (!Enum.TryParse(statusParam, true, out newStatus))
            {
                context.Response.StatusCode = 400;
                error = true;
            }
            else
            {
                int id = int.Parse(idStr);
                foreach (KeyValuePair<string, Task> kvp in taskMap)
                {
                    if (kvp.Value.id == id)
                    {
                        oldStatus = kvp.Value.eStatus.ToString();
                        kvp.Value.eStatus = newStatus;
                        break;
                    }
                }
                context.Response.StatusCode = 200;
                createJsonResponse("result", oldStatus);
            }

            LogTODO(LogLevel.INFO, $"Update TODO id [{idStr}] state to {statusParam}");
            if (!error && todoDefLevel <= LogLevel.DEBUG)
            {
                LogTODO(LogLevel.DEBUG, $"Todo id [{idStr}] state change: {oldStatus} --> {statusParam}");
            }
            else if (error)
            {
                LogTODO(LogLevel.ERROR, errorMsg);
            }
        }

        private static bool idIsExist(string idStr)
        {
            bool exist = false;
            int id = int.Parse(idStr);
            foreach (KeyValuePair<string, Task> kvp in taskMap)
            {
                if (kvp.Value.id == id)
                {
                    exist = true;
                }
            }
            return exist;
        }

        private static void getTODOsData()
        {
            Status status;
            sortBy eSortBy;
            string statusParam = context.Request.QueryString["status"];
            string sortByParam = context.Request.QueryString["sortBy"];
            if (statusOutOfRange(statusParam) || (sortByParam != null && sortByOutOfRange(sortByParam)))
            {
                context.Response.StatusCode = 400;
            }
            else
            {
                Enum.TryParse(statusParam, true, out status);
                Enum.TryParse(sortByParam, true, out eSortBy);
                context.Response.StatusCode = 200;
                List<Task> TODOsContent = filterAndSortTODOs(status, eSortBy);
                JArray arr = new JArray(new JToken[TODOsContent.Count]);
                for (int i = 0; i < TODOsContent.Count; i++)
                {
                    Task task = TODOsContent[i];
                    JObject obj = new JObject();
                    obj.Add("id", task.id);
                    obj.Add("title", task.title);
                    obj.Add("content", task.content);
                    obj.Add("status", task.eStatus.ToString());
                    obj.Add("dueDate", task.dueDate);
                    arr[i] = obj;
                }
                string jsonResponse = JsonConvert.SerializeObject(arr);
                byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();

                LogTODO(LogLevel.INFO, $"Extracting todos content. Filter: {statusParam} | Sorting by: {eSortBy}");
                if (todoDefLevel <= LogLevel.DEBUG)
                {
                    LogTODO(LogLevel.DEBUG, $"There are a total of {taskMap.Count} todos in the system." +
                        $" The result holds {TODOsContent.Count} todos");
                }
            }
        }

        private static List<Task> filterAndSortTODOs(Status status, sortBy eSortBy)
        {
            List<Task> res = new List<Task>();
            foreach (KeyValuePair<string, Task> kvp in taskMap)
            {
                if (kvp.Value.eStatus.ToString() == status.ToString() || status.ToString() == "ALL")
                {
                    res.Add(kvp.Value);
                }
            }
            res.Sort(new TaskComparer(eSortBy));
            return res;
        }

        private static bool sortByOutOfRange(string sortBy)
        {
            bool outOfRange = false;
            if (sortBy != "ID" && sortBy != "DUE_DATE" && sortBy != "TITLE")
            {
                outOfRange = true;
            }
            return outOfRange;
        }

        private static bool statusOutOfRange(string status)
        {
            bool outOfRange = false;
            if (status != "ALL" && status != "PENDING" && status != "LATE" && status != "DONE")
            {
                outOfRange = true;
            }
            return outOfRange;
        }

        private static void getTODOsCount()
        {
            Status status;
            string statusParam = context.Request.QueryString["status"];
            if (statusOutOfRange(statusParam))
            {
                context.Response.StatusCode = 400;
            }
            else
            {
                context.Response.StatusCode = 200;
                Enum.TryParse(statusParam, true, out status);
                int totalTODOSReturned = 0;
                switch (status)
                {
                    case Status.ALL:
                        totalTODOSReturned = taskMap.Count;
                        createJsonResponse("result", totalTODOSReturned.ToString());
                        break;
                    case Status.PENDING:
                        totalTODOSReturned = countStatus(Status.PENDING.ToString());
                        createJsonResponse("result", totalTODOSReturned.ToString());
                        break;
                    case Status.LATE:
                        totalTODOSReturned = countStatus(Status.LATE.ToString());
                        createJsonResponse("result", totalTODOSReturned.ToString());
                        break;
                    case Status.DONE:
                        totalTODOSReturned = countStatus(Status.DONE.ToString());
                        createJsonResponse("result", totalTODOSReturned.ToString());
                        break;
                }

                LogTODO(LogLevel.INFO, $"Total TODOs count for state {status} is {totalTODOSReturned}");
            }
        }

        private static int countStatus(string toCheck)
        {
            int counter = 0;
            foreach (KeyValuePair<string, Task> kvp in taskMap)
            {
                if (kvp.Value.eStatus.ToString() == toCheck)
                {
                    counter++;
                }
            }
            return counter;
        }

        private static void createTODO()
        {
            string requestBody;
            using (StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
            {
                requestBody = reader.ReadToEnd();
            }
            JObject requestBodyObject = JObject.Parse(requestBody);
            string title = requestBodyObject["title"].ToString();
            string content = requestBodyObject["content"].ToString();
            long dueDate = (long)requestBodyObject["dueDate"];
            string errorStr = "";
            if (goodInput(title, dueDate, ref errorStr))
            {
                Task newTask = new Task(title, content, dueDate);
                taskMap.Add(newTask.title, newTask);
                context.Response.StatusCode = 200;
                createJsonResponse("result", newTask.id.ToString());

                LogTODO(LogLevel.INFO, $"Creating new TODO with Title [{title}]");
                if (todoDefLevel <= LogLevel.DEBUG)
                {
                    LogTODO(LogLevel.DEBUG, $"Currently there are {taskMap.Count - 1} TODOs in the system. New TODO will be assigned with id {newTask.id}");
                }
            }
            else
            {
                LogTODO(LogLevel.ERROR, errorStr);
            }
        }

        private static bool goodInput(string title, long dueDate, ref string errorStr)
        {
            bool goodInput = true;
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if ((taskMap.ContainsKey(title)) || (dueDate <= currentTimestamp))
            {
                goodInput = false;
                if (taskMap.ContainsKey(title))
                {
                    errorStr = $"Error: TODO with the title {title} already exists in the system";
                }
                else
                {
                    errorStr = "Error: Can’t create new TODO that its due date is in the past";
                }
                createJsonResponse("errorMessage", errorStr);
                context.Response.StatusCode = 409;
            }
            return goodInput;
        }

        private static void createJsonResponse(string key, string value)
        {
            result.RemoveAll();
            result.Add(key, value);
            byte[] buffer = Encoding.UTF8.GetBytes(result.ToString());
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        private static void checkServerHealth()
        {
            context.Response.StatusCode = 200;
            byte[] buffer = Encoding.UTF8.GetBytes("OK");
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private static void startServer()
        {
            Stopwatch sw = new Stopwatch();

            sw.Start();
            server = new HttpListener();
            server.Prefixes.Add("http://localhost:9583/");
            server.Start();
            sw.Stop();
            Console.WriteLine("Server listening on port 9583...");
            Console.WriteLine($"It took {sw.ElapsedMilliseconds} milli sec's to start the server");
            string logsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logsDirectory))
            {
                Directory.CreateDirectory(logsDirectory);
            }
            else if (firstRun)
            {
                firstRun = false;
                string[] logFiles = Directory.GetFiles(logsDirectory);
                foreach (string file in logFiles)
                {
                    File.Delete(file);
                }
            }
        }

        private static endPoint getEndPoint(HttpListenerContext context)
        {
            endPoint res = endPoint.NULL;
            if (context.Request.Url.AbsolutePath == "/todo/health")
            {
                res = endPoint.Health;
            }
            else if (context.Request.Url.AbsolutePath == "/todo")
            {
                if (context.Request.HttpMethod == "POST")
                {
                    res = endPoint.Create;
                }
                else if (context.Request.HttpMethod == "PUT")
                {
                    res = endPoint.Update;
                }
                else if (context.Request.HttpMethod == "DELETE")
                {
                    res = endPoint.Delete;
                }
            }
            else if (context.Request.Url.AbsolutePath == "/todo/size")
            {
                res = endPoint.Size;
            }
            else if (context.Request.Url.AbsolutePath == "/todo/content")
            {
                res = endPoint.Content;
            }
            else if (context.Request.Url.AbsolutePath == "/logs/level")
            {
                if (context.Request.HttpMethod == "GET")
                {
                    res = endPoint.GetLog;
                }
                else if (context.Request.HttpMethod == "PUT")
                {
                    res = endPoint.PutLog;
                }
            }
            return res;
        }

        enum endPoint
        {
            Health,
            Create,
            Size,
            Content,
            Update,
            Delete,
            GetLog,
            PutLog,
            NULL
        }
        enum Status
        {
            PENDING,
            LATE,
            DONE,
            ALL
        }
        public enum sortBy
        {
            ID,
            DUE_DATE,
            TITLE
        }
        public enum LogLevel
        {
            DEBUG,
            INFO,
            ERROR,
        }
    }

    class Task
    {
        public string title, content;
        public long dueDate;
        public Status eStatus;
        public int id;

        private static int counter = 0;

        public Task(string title, string content, long dueDate)
        {
            this.title = title;
            this.content = content;
            this.dueDate = dueDate;
            id = ++counter;
            eStatus = Status.PENDING;
        }

        public enum Status
        {
            PENDING,
            LATE,
            DONE
        }
    }

    class TaskComparer : IComparer<Task>
    {
        private kaplat_server.Program.sortBy eSortBy;
        public TaskComparer(kaplat_server.Program.sortBy sortBy)
        {
            eSortBy = sortBy;
        }

        public int Compare(Task x, Task y)
        {
            switch (eSortBy)
            {
                case kaplat_server.Program.sortBy.ID:
                    return x.id.CompareTo(y.id);
                case kaplat_server.Program.sortBy.DUE_DATE:
                    return x.dueDate.CompareTo(y.dueDate);
                case kaplat_server.Program.sortBy.TITLE:
                    return string.Compare(x.title, y.title);
                default:
                    throw new ArgumentException("Invalid sort by value");
            }
        }
    }
}