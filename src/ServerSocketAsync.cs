using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class ServerSocketAsync : IDisposable {

	private TcpListener _tcpListener;
	private Dictionary<int, AcceptSocket> _clients = new Dictionary<int, AcceptSocket>();
	private object _clients_lock = new object();
	private int _id = 1;
	private int _port;
	private bool _running;
	public event AcceptedEventHandler Accepted;
	public event ClosedEventHandler Closed;
	public event ReceiveEventHandler Receive;
	public event ErrorEventHandler Error;

	internal WorkQueue _receiveWQ;
	internal WorkQueue _receiveSyncWQ;
	private WorkQueue _writeWQ;

	private IAsyncResult _beginAcceptTcpClient;

	public ServerSocketAsync(int port) {
		this._port = port;
	}

	public void Start() {
		if (this._running == false) {
			this._running = true;
			try {
				this._tcpListener = new TcpListener(IPAddress.Any, this._port);
				this._tcpListener.Start();
				this._receiveWQ = new WorkQueue();
				this._receiveSyncWQ = new WorkQueue();
				this._writeWQ = new WorkQueue();
			} catch (Exception ex) {
				this._running = false;
				this.OnError(ex);
				return;
			}
			this._beginAcceptTcpClient = this._tcpListener.BeginAcceptTcpClient(HandleTcpClientAccepted, null);
		}
	}

	private void HandleTcpClientAccepted(IAsyncResult ar) {
		if (this._running) {
			try {
				TcpClient tcpClient = this._tcpListener.EndAcceptTcpClient(ar);

				try {
					AcceptSocket acceptSocket = new AcceptSocket(this, tcpClient, this._id);
					this.OnAccepted(acceptSocket);
				} catch (Exception ex) {
					this.OnError(ex);
				}

				this._beginAcceptTcpClient = this._tcpListener.BeginAcceptTcpClient(HandleTcpClientAccepted, null);
			} catch (Exception ex) {
				this.OnError(ex);
			}
		}
	}
	public void Stop() {
		if (this._tcpListener != null) {
			this._tcpListener.Stop();
		}
		if (this._running == true) {
			this._beginAcceptTcpClient.AsyncWaitHandle.Close();

			this._running = false;

			int[] keys = new int[this._clients.Count];
			try {
				this._clients.Keys.CopyTo(keys, 0);
			} catch {
				lock (this._clients_lock) {
					keys = new int[this._clients.Count];
					this._clients.Keys.CopyTo(keys, 0);
				}
			}
			foreach (int key in keys) {
				AcceptSocket client = null;
				if (this._clients.TryGetValue(key, out client)) {
					client.Close();
				}
			}
			if (this._receiveWQ != null) {
				this._receiveWQ.Dispose();
			}
			if (this._receiveSyncWQ != null) {
				this._receiveSyncWQ.Dispose();
			}
			if (this._writeWQ != null) {
				this._writeWQ.Dispose();
			}
			this._clients.Clear();
		}
	}

	internal void AccessDenied(AcceptSocket client) {
		client.Write(SocketMessager.SYS_ACCESS_DENIED, delegate(object sender2, ReceiveEventArgs e2) {
		}, TimeSpan.FromSeconds(1));
		client.Close();
	}

	public void Write(SocketMessager messager) {
		int[] keys = new int[this._clients.Count];
		try {
			this._clients.Keys.CopyTo(keys, 0);
		} catch {
			lock (this._clients_lock) {
				keys = new int[this._clients.Count];
				this._clients.Keys.CopyTo(keys, 0);
			}
		}
		foreach (int key in keys) {
			AcceptSocket client = null;
			if (this._clients.TryGetValue(key, out client)) {
				this._writeWQ.Enqueue(delegate() {
					client.Write(messager);
				});
			}
		}
	}

	public AcceptSocket GetAcceptSocket(int id) {
		AcceptSocket socket = null;
		this._clients.TryGetValue(id, out socket);
		return socket;
	}

	internal void CloseClient(AcceptSocket client) {
		this._clients.Remove(client.Id);
	}

	protected virtual void OnAccepted(AcceptedEventArgs e) {
		SocketMessager helloMessager = new SocketMessager(SocketMessager.SYS_HELLO_WELCOME.Action);
		e.AcceptSocket.Write(helloMessager, delegate(object sender2, ReceiveEventArgs e2) {
			if (e2.Messager.Id == helloMessager.Id &&
				string.Compare(e2.Messager.Action, helloMessager.Action) == 0) {
				e.AcceptSocket._accepted = true;
			}
		}, TimeSpan.FromSeconds(2));
		if (e.AcceptSocket._accepted) {
			if (this.Accepted != null) {
				try {
					this.Accepted(this, e);
				} catch (Exception ex) {
					this.OnError(ex);
				}
			}
		} else {
			e.AcceptSocket.AccessDenied();
		}
	}
	private void OnAccepted(AcceptSocket client) {
		lock (_clients_lock) {
			_clients.Add(this._id++, client);
		}
		AcceptedEventArgs e = new AcceptedEventArgs(this._clients.Count, client);
		this.OnAccepted(e);
	}

	protected virtual void OnClosed(ClosedEventArgs e) {
		if (this.Closed != null) {
			this.Closed(this, e);
		}
	}
	internal void OnClosed(AcceptSocket client) {
		ClosedEventArgs e = new ClosedEventArgs(this._clients.Count, client.Id);
		this.OnClosed(e);
	}

	protected virtual void OnReceive(ReceiveEventArgs e) {
		if (this.Receive != null) {
			this.Receive(this, e);
		}
	}
	internal void OnReceive2(ReceiveEventArgs e) {
		this.OnReceive(e);
	}

	protected virtual void OnError(ErrorEventArgs e) {
		if (this.Error != null) {
			this.Error(this, e);
		}
	}
	protected void OnError(Exception ex) {
		ErrorEventArgs e = new ErrorEventArgs(-1, ex, null);
		this.OnError(e);
	}
	internal void OnError2(ErrorEventArgs e) {
		this.OnError(e);
	}

	#region IDisposable 成员

	public void Dispose() {
		this.Stop();
	}

	#endregion

	public class AcceptSocket : BaseSocket, IDisposable {

		private ServerSocketAsync _server;
		private TcpClient _tcpClient;
		private bool _running;
		private int _id;
		private int _receives;
		private int _errors;
		private object _errors_lock = new object();
		private object _write_lock = new object();
		private Dictionary<int, SyncReceive> _receiveHandlers = new Dictionary<int, SyncReceive>();
		private object _receiveHandlers_lock = new object();
		private DateTime _lastActive;
		internal bool _accepted;
		internal IAsyncResult _beginRead;

		public AcceptSocket(ServerSocketAsync server, TcpClient tcpClient, int id) {
			this._running = true;
			this._id = id;
			this._server = server;
			this._tcpClient = tcpClient;
			this._lastActive = DateTime.Now;
			HandleDataReceived();
		}

		private void HandleDataReceived() {
			if (this._running) {
				try {
					NetworkStream ns = this._tcpClient.GetStream();
					ns.ReadTimeout = 1000 * 20;

					DataReadInfo dr = new DataReadInfo(DataReadInfoType.Head, this, ns, BaseSocket.HeadLength, BaseSocket.HeadLength);
					dr.BeginRead();

				} catch (Exception ex) {
					this._running = false;
					this.OnError(ex);
				}
			}
		}

		private void OnDataAvailable(DataReadInfo dr) {
			SocketMessager messager = SocketMessager.Parse(dr.ResponseStream.ToArray());
			if (string.Compare(messager.Action, SocketMessager.SYS_QUIT.Action) == 0) {
				dr.AcceptSocket.Close();
			} else if (string.Compare(messager.Action, SocketMessager.SYS_TEST_LINK.Action) != 0) {
				ReceiveEventArgs e = new ReceiveEventArgs(this._receives++, messager, this);
				SyncReceive receive = null;

				if (this._receiveHandlers.TryGetValue(messager.Id, out receive)) {
					this._server._receiveSyncWQ.Enqueue(delegate () {
						try {
							receive.ReceiveHandler(this, e);
						} catch (Exception ex) {
							this.OnError(ex);
						} finally {
							receive.Wait.Set();
						}
					});
				} else {
					this._server._receiveWQ.Enqueue(delegate () {
						this.OnReceive(e);
					});
				}
			}
			this._lastActive = DateTime.Now;
			HandleDataReceived();
		}

		class DataReadInfo {
			public DataReadInfoType Type { get; set; }
			public AcceptSocket AcceptSocket { get; }
			public NetworkStream NetworkStream { get; }
			public byte[] Buffer { get; }
			public int Size { get; }
			public int Over { get; set; }
			public MemoryStream ResponseStream { get; set; }
			public DataReadInfo(DataReadInfoType type, AcceptSocket client, NetworkStream ns, int bufferSize, int size) {
				this.Type = type;
				this.AcceptSocket = client;
				this.NetworkStream = ns;
				this.Buffer = new byte[bufferSize];
				this.Size = size;
				this.Over = size;
				this.ResponseStream = new MemoryStream();
			}

			public void BeginRead() {
				this.AcceptSocket._beginRead = this.NetworkStream.BeginRead(this.Buffer, 0, this.Over < this.Buffer.Length ? this.Over : this.Buffer.Length, HandleDataRead, this);
			}
		}
		enum DataReadInfoType { Head, Body }

		static void HandleDataRead(IAsyncResult ar) {
			DataReadInfo dr = ar.AsyncState as DataReadInfo;
			if (dr.AcceptSocket._running) {
				int overs = 0;
				try {
					overs = dr.NetworkStream.EndRead(ar);
				} catch(Exception ex) {
					dr.AcceptSocket.OnError(ex);
					return;
				}
				if (overs > 0) dr.ResponseStream.Write(dr.Buffer, 0, overs);

				dr.Over -= overs;
				if (dr.Over > 0) {
					dr.BeginRead();

				} else if (dr.Type == DataReadInfoType.Head) {

					var bodySizeBuffer = dr.ResponseStream.ToArray();
					if (int.TryParse(Encoding.UTF8.GetString(bodySizeBuffer, 0, bodySizeBuffer.Length), NumberStyles.HexNumber, null, out overs)) {
						DataReadInfo drBody = new DataReadInfo(DataReadInfoType.Body, dr.AcceptSocket, dr.NetworkStream, 1024, overs - BaseSocket.HeadLength);
						drBody.BeginRead();
					}
				} else {
					dr.AcceptSocket.OnDataAvailable(dr);
				}
			}
		}

		public void Close() {
			if (this._running == true) {
				this._beginRead.AsyncWaitHandle.Close();

				this._running = false;
				if (this._tcpClient != null) {
					this._tcpClient.Dispose();
					this._tcpClient = null;
				}
				this.OnClosed();
				this._server.CloseClient(this);
				int[] keys = new int[this._receiveHandlers.Count];
				try {
					this._receiveHandlers.Keys.CopyTo(keys, 0);
				} catch {
					lock (this._receiveHandlers_lock) {
						keys = new int[this._receiveHandlers.Count];
						this._receiveHandlers.Keys.CopyTo(keys, 0);
					}
				}
				foreach (int key in keys) {
					SyncReceive receiveHandler = null;
					if (this._receiveHandlers.TryGetValue(key, out receiveHandler)) {
						receiveHandler.Wait.Set();
					}
				}
				lock (this._receiveHandlers_lock) {
					this._receiveHandlers.Clear();
				}
			}
		}

		public void Write(SocketMessager messager) {
			this.Write(messager, null, TimeSpan.Zero);
		}
		public void Write(SocketMessager messager, ReceiveEventHandler receiveHandler) {
			this.Write(messager, receiveHandler, TimeSpan.FromSeconds(20));
		}
		public void Write(SocketMessager messager, ReceiveEventHandler receiveHandler, TimeSpan timeout) {
			SyncReceive syncReceive = null;
			try {
				if (receiveHandler != null) {
					syncReceive = new SyncReceive(receiveHandler);
					lock (this._receiveHandlers_lock) {
						if (!this._receiveHandlers.ContainsKey(messager.Id)) {
							this._receiveHandlers.Add(messager.Id, syncReceive);
						} else {
							this._receiveHandlers[messager.Id] = syncReceive;
						}
					}
				}
				if (this._running) {
					lock (_write_lock) {
						NetworkStream ns = this._tcpClient.GetStream();
						base.WriteAsync(ns, messager);
					}
					this._lastActive = DateTime.Now;

					if (syncReceive != null) {
						syncReceive.Wait.Reset();
						syncReceive.Wait.WaitOne(timeout);
						syncReceive.Wait.Set();
						lock (this._receiveHandlers_lock) {
							this._receiveHandlers.Remove(messager.Id);
						}
					}
				}
			} catch (Exception ex) {
				this._running = false;
				this.OnError(ex);
				if (syncReceive != null) {
					syncReceive.Wait.Set();
					lock (this._receiveHandlers_lock) {
						this._receiveHandlers.Remove(messager.Id);
					}
				}
			}
		}

		/// <summary>
		/// 拒绝访问，并关闭连接
		/// </summary>
		public void AccessDenied() {
			this._server.AccessDenied(this);
		}

		protected virtual void OnClosed() {
			try {
				this._server.OnClosed(this);
			} catch (Exception ex) {
				this.OnError(ex);
			}
		}

		protected virtual void OnReceive(ReceiveEventArgs e) {
			try {
				this._server.OnReceive2(e);
			} catch (Exception ex) {
				this.OnError(ex);
			}
		}

		protected virtual void OnError(Exception ex) {
			int errors = 0;
			lock (this._errors_lock) {
				errors = ++this._errors;
			}
			ErrorEventArgs e = new ErrorEventArgs(errors, ex, this);
			this._server.OnError2(e);
		}

		public int Id {
			get { return _id; }
		}

		class SyncReceive : IDisposable {
			private ReceiveEventHandler _receiveHandler;
			private ManualResetEvent _wait;

			public SyncReceive(ReceiveEventHandler onReceive) {
				this._receiveHandler = onReceive;
				this._wait = new ManualResetEvent(false);
			}

			public ManualResetEvent Wait {
				get { return _wait; }
			}
			public ReceiveEventHandler ReceiveHandler {
				get { return _receiveHandler; }
			}

			#region IDisposable 成员

			public void Dispose() {
				this._wait.Set();
			}

			#endregion
		}

		#region IDisposable 成员

		void IDisposable.Dispose() {
			this.Close();
		}

		#endregion
	}

	public delegate void ClosedEventHandler(object sender, ClosedEventArgs e);
	public delegate void AcceptedEventHandler(object sender, AcceptedEventArgs e);
	public delegate void ErrorEventHandler(object sender, ErrorEventArgs e);
	public delegate void ReceiveEventHandler(object sender, ReceiveEventArgs e);

	public class ClosedEventArgs : EventArgs {

		private int _accepts;
		private int _acceptSocketId;

		public ClosedEventArgs(int accepts, int acceptSocketId) {
			this._accepts = accepts;
			this._acceptSocketId = acceptSocketId;
		}

		public int Accepts {
			get { return _accepts; }
		}
		public int AcceptSocketId {
			get { return _acceptSocketId; }
		}
	}

	public class AcceptedEventArgs : EventArgs {

		private int _accepts;
		private AcceptSocket _acceptSocket;

		public AcceptedEventArgs(int accepts, AcceptSocket acceptSocket) {
			this._accepts = accepts;
			this._acceptSocket = acceptSocket;
		}

		public int Accepts {
			get { return _accepts; }
		}
		public AcceptSocket AcceptSocket {
			get { return _acceptSocket; }
		}
	}

	public class ErrorEventArgs : EventArgs {

		private int _errors;
		private Exception _exception;
		private AcceptSocket _acceptSocket;

		public ErrorEventArgs(int errors, Exception exception, AcceptSocket acceptSocket) {
			this._errors = errors;
			this._exception = exception;
			this._acceptSocket = acceptSocket;
		}

		public int Errors {
			get { return _errors; }
		}
		public Exception Exception {
			get { return _exception; }
		}
		public AcceptSocket AcceptSocket {
			get { return _acceptSocket; }
		}
	}

	public class ReceiveEventArgs : EventArgs {

		private int _receives;
		private SocketMessager _messager;
		private AcceptSocket _acceptSocket;

		public ReceiveEventArgs(int receives, SocketMessager messager, AcceptSocket acceptSocket) {
			this._receives = receives;
			this._messager = messager;
			this._acceptSocket = acceptSocket;
		}

		public int Receives {
			get { return _receives; }
		}
		public SocketMessager Messager {
			get { return _messager; }
		}
		public AcceptSocket AcceptSocket {
			get { return _acceptSocket; }
		}
	}
}
