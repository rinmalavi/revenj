﻿using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Security;
using System.ServiceModel;
using System.Text;
using System.Threading;
using Revenj.Api;
using Revenj.DomainPatterns;
using Revenj.Logging;
using Revenj.Utility;

namespace Revenj.Http
{
	public class HttpServer
	{
		private readonly HttpListener Listener;
		private readonly ILogger Logger;
		private readonly Routes Routes;
		private readonly HttpAuth Authentication;

		public HttpServer(ILogFactory logFactory, IServiceLocator locator)
		{
			Logger = logFactory.Create("Http server");
			Listener = new HttpListener();
			Listener.IgnoreWriteExceptions = true;
			foreach (string key in ConfigurationManager.AppSettings.Keys)
			{
				if (key.StartsWith("HttpAddress", StringComparison.InvariantCultureIgnoreCase))
					Listener.Prefixes.Add(ConfigurationManager.AppSettings[key]);
			}
			if (Listener.Prefixes.Count == 0)
			{
				Listener.Prefixes.Add("http://*:80/");
				Listener.Prefixes.Add("https://*:443/");
			}
			Routes = new Routes(locator);
			var customAuth = ConfigurationManager.AppSettings["CustomAuth"];
			if (!string.IsNullOrEmpty(customAuth))
			{
				var authType = Type.GetType(customAuth);
				if (!typeof(HttpAuth).IsAssignableFrom(authType))
					throw new ConfigurationErrorsException("Custom auth does not inherit from HttpAuth. Please inherit from " + typeof(HttpAuth).FullName);
				Authentication = locator.Resolve<HttpAuth>(authType);
			}
			else Authentication = locator.Resolve<HttpAuth>();
		}

		public void Run()
		{
			try
			{
				Listener.Start();
				Console.WriteLine("Server running on:");
				foreach (var url in Listener.Prefixes)
					Console.WriteLine(url);
				while (true)
				{
					try
					{
						var context = Listener.GetContext();
						ThreadPool.QueueUserWorkItem(ProcessMessageThread, context);
						context = null;
					}
					catch (HttpListenerException ex)
					{
						Console.WriteLine(ex.ToString());
						Logger.Error(ex.ToString());
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
				Logger.Error(ex.ToString());
				throw;
			}
			finally
			{
				Listener.Close();
			}
		}

		private void ProcessMessageThread(object state)
		{
			var context = (HttpListenerContext)state;
			var request = context.Request;
			var response = context.Response;
			try
			{
				RouteMatch match;
				var route = Routes.Find(request, out match);
				if (route != null)
				{
					var auth = Authentication.TryAuthorize(context, route);
					if (auth.Principal != null)
					{
						var ctx = new HttpThreadContex(request, response, match);
						ThreadContext.Request = ctx;
						ThreadContext.Response = ctx;
						Thread.CurrentPrincipal = auth.Principal;
						using (var stream = route.Handle(match.BoundVars, context))
						{
							var cms = stream as ChunkedMemoryStream;
							if (cms != null)
							{
								response.ContentLength64 = cms.Length;
								cms.CopyTo(response.OutputStream);
							}
							else
							{
								if (stream.CanSeek)
									response.ContentLength64 = stream.Length;
								stream.CopyTo(response.OutputStream);
							}
						}
					}
					else ReturnError(response, (int)auth.ResponseCode, auth.Error);
				}
				else
				{
					var unknownRoute = "Unknown route " + request.RawUrl + " on method " + request.HttpMethod;
					ReturnError(response, 404, unknownRoute);
				}
			}
			catch (SecurityException sex)
			{
				ReturnError(response, (int)HttpStatusCode.Forbidden, sex.Message);
			}
			catch (ActionNotSupportedException anse)
			{
				ReturnError(response, 404, anse.Message);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				Logger.Error(ex.ToString());
				ReturnError(response, 500, ex.Message);
			}
			finally
			{
				response.Close();
			}
		}

		private void ReturnError(HttpListenerResponse response, int status, string message)
		{
			// Response is disposed before ReturnError is called when sent request is invalid.
			// This inner try-catch prevents application crash because of that.
			try {
				response.StatusCode = status;
				response.ContentType = "text/plain; charset=\"utf-8\"";
				response.ContentLength64 = Encoding.UTF8.GetByteCount(message);
				using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(message)))
					ms.CopyTo(response.OutputStream);
			} catch (Exception ex) {
				Console.WriteLine(ex.Message);
				Logger.Error(ex.ToString());
			}
		}
	}
}
