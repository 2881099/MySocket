Socket服务端与客户端的封装，支持.NETCore

# 安装

> Install-Package MySocket

# 服务端(多线程)

```csharp
var server = new ServerSocket(19990); //监听0.0.0.0:19990
server.Receive += (a, b) => {
	Console.WriteLine("{0} 接受到了消息{1}：{2}", DateTime.Now, b.Receives, b.Messager);
	b.AcceptSocket.Write(b.Messager);
};
server.Accepted += (a, b) => {
	Console.WriteLine("{0} 新连接：{1}", DateTime.Now, b.Accepts);
};
server.Closed += (a, b) => {
	Console.WriteLine("{0} 关闭了连接：{1}", DateTime.Now, b.AcceptSocketId);
};
server.Error += (a, b) => {
	Console.WriteLine("{0} 发生错误({1})：{2}", DateTime.Now, b.Errors, 
		b.Exception.Message + b.Exception.StackTrace);
};
server.Start();
Console.ReadKey();
```

# 服务端(异步)，理论上性能最强

```csharp
var server = new ServerSocketAsync(19990); //监听0.0.0.0:19990
server.Receive += (a, b) => {
	Console.WriteLine("{0} 接受到了消息{1}：{2}", DateTime.Now, b.Receives, b.Messager);
	b.AcceptSocket.Write(b.Messager);
};
server.Accepted += (a, b) => {
	Console.WriteLine("{0} 新连接：{1}", DateTime.Now, b.Accepts);
};
server.Closed += (a, b) => {
	Console.WriteLine("{0} 关闭了连接：{1}", DateTime.Now, b.AcceptSocketId);
};
server.Error += (a, b) => {
	Console.WriteLine("{0} 发生错误({1})：{2}", DateTime.Now, b.Errors, 
		b.Exception.Message + b.Exception.StackTrace);
};
server.Start();
Console.ReadKey();
```

# 客户端

```csharp
var client = new ClientSocket();
client.Error += (sender, e) => {
	Console.WriteLine("[" + DateTime.Now.ToString("MM-dd HH:mm:ss") + "] " 
		+ e.Exception.Message + e.Exception.StackTrace);
};
client.Receive += (sender, e) => {
	switch (e.Messager.Action) {
	}
};
client.Connect("localhost", 19990);

SocketMessager messager = new SocketMessager("GetDatabases", 1);
object dbs = null;
//以下代码等于同步，直到服务端响应(会执行委托)或超时
client.Write(messager, (sender2, e2) => {
	//服务端正常响应会执行这里
	dbs = e2.Messager;
});
Console.WriteLine(dbs);
Console.WriteLine("sldkjglsjdglksdg");
//若不传递第二个委托参数，线程不会等待结果，服务端响应后由 client.Receive 处理
//Console.ReadKey();
client.Close();
```