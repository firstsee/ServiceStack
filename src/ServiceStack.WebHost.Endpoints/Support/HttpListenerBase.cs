﻿using System;
using System.IO;
using System.Net;
using System.Reflection;
using Funq;
using ServiceStack.Logging;
using ServiceStack.ServiceHost;
using ServiceStack.ServiceModel.Serialization;

namespace ServiceStack.WebHost.Endpoints.Support
{
	public delegate void DelReceiveWebRequest(HttpListenerContext context);

	/// <summary>
	/// Wrapper class for the HTTPListener to allow easier access to the
	/// server, for start and stop management and event routing of the actual
	/// inbound requests.
	/// </summary>
	public abstract class HttpListenerBase : IDisposable
	{
		private readonly ILog log = LogManager.GetLogger(typeof(HttpListenerBase));

		private const int RequestThreadAbortedException = 995;

		protected HttpListener Listener;
		protected bool IsStarted = false;

		private readonly DateTime startTime;
		//private readonly ServiceManager serviceManager;
		public static HttpListenerBase Instance { get; protected set; }

		public event DelReceiveWebRequest ReceiveWebRequest;

		protected HttpListenerBase()
		{
			this.startTime = DateTime.Now;
			log.Info("Begin Initializing Application...");
		}

		protected HttpListenerBase(string serviceName, params Assembly[] assembliesWithServices)
			: this()
		{
			SetConfig(new EndpointHostConfig {
				ServiceName = serviceName,
				ServiceManager = new ServiceManager(assembliesWithServices),
			});
		}

		public void Init()
		{
			if (Instance != null)
			{
				throw new InvalidDataException("HttpListenerBase.Instance has already been set");
			}

			Instance = this;

			var serviceManager = EndpointHost.Config.ServiceManager;
			if (serviceManager != null)
			{
				serviceManager.Init();
				Configure(EndpointHost.Config.ServiceManager.Container);

				EndpointHost.SetOperationTypes(
					serviceManager.ServiceOperations,
					serviceManager.AllServiceOperations
				);
			}
			else
			{
				Configure(null);
			}

			var elapsed = DateTime.Now - this.startTime;
			log.InfoFormat("Initializing Application took {0}ms", elapsed.TotalMilliseconds);
		}

		public abstract void Configure(Container container);

		/// <summary>
		/// Starts the Web Service
		/// </summary>
		/// <param name="urlBase">
		/// A Uri that acts as the base that the server is listening on.
		/// Format should be: http://127.0.0.1:8080/ or http://127.0.0.1:8080/somevirtual/
		/// Note: the trailing backslash is required! For more info see the
		/// HttpListener.Prefixes property on MSDN.
		/// </param>
		public virtual void Start(string urlBase)
		{
			// *** Already running - just leave it in place
			if (this.IsStarted)
				return;

			if (this.Listener == null)
			{
				this.Listener = new HttpListener();
			}

			this.Listener.Prefixes.Add(urlBase);

			this.IsStarted = true;
			this.Listener.Start();

			var result = this.Listener.BeginGetContext(
				WebRequestCallback, this.Listener);
		}

		/// <summary>
		/// Shut down the Web Service
		/// </summary>
		public virtual void Stop()
		{
			if (Listener == null) return;

			try
			{
				this.Listener.Close();
			}
			catch (HttpListenerException ex)
			{
				if (ex.ErrorCode != RequestThreadAbortedException)
					throw;

				log.ErrorFormat("Swallowing HttpListenerException({0}) Thread exit or aborted request",
								RequestThreadAbortedException);
			}
			this.Listener = null;
			this.IsStarted = false;
		}

		private void WriteException(HttpListenerContext context, Exception ex)
		{
			if (context == null) throw ex;

			try
			{
				using (var sw = new StreamWriter(context.Response.OutputStream))
				{
					sw.WriteLine("Error: " + ex.Message + "\r\nStackTrace:" + ex.StackTrace);
				}
				context.Response.Close();
			}
			catch(Exception writeEx)
			{
				log.Error("Error writing Exception to the response: " + writeEx.Message, writeEx);
			}
		}

		protected void WebRequestCallback(IAsyncResult result)
		{
			if (this.Listener == null)
				return;

			HttpListenerContext context = null;
			try
			{
				// Get out the context object
				context = this.Listener.EndGetContext(result);

				// *** Immediately set up the next context
				this.Listener.BeginGetContext(WebRequestCallback, this.Listener);

				if (this.ReceiveWebRequest != null)
					this.ReceiveWebRequest(context);

				this.ProcessRequest(context);

			}
			catch (HttpListenerException ex)
			{
				//if (ex.ErrorCode != RequestThreadAbortedException)
				//    throw;

				log.Error(string.Format("Swallowing HttpListenerException({0}) Thread exit or aborted request",
								RequestThreadAbortedException), ex);
				WriteException(context, ex);
			}
			catch (Exception ex)
			{
				log.Error("Swallowing Exception: " + ex.Message, ex);
				WriteException(context, ex);
			}
		}

		/// <summary>
		/// Overridable method that can be used to implement a custom hnandler
		/// </summary>
		/// <param name="context"></param>
		protected abstract void ProcessRequest(HttpListenerContext context);

		protected void SetConfig(EndpointHostConfig config)
		{
			if (config.ServiceName == null)
				config.ServiceName = EndpointHost.Config.ServiceName;

			if (config.ServiceManager == null)
				config.ServiceManager = EndpointHost.Config.ServiceManager;

			config.ServiceManager.ServiceController.EnableAccessRestrictions = config.EnableAccessRestrictions;

			EndpointHost.Config = config;

			JsonDataContractSerializer.Instance.UseBcl = config.UseBclJsonSerializers;
			JsonDataContractDeserializer.Instance.UseBcl = config.UseBclJsonSerializers;
		}

		public virtual void Dispose()
		{
			this.Stop();

			if (EndpointHost.Config.ServiceManager != null)
			{
				EndpointHost.Config.ServiceManager.Dispose();
			}
		}
	}
}