using System;

namespace TestServer
{
    class Program
    {
        static void Main(string[] args)
        {

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
        }
    }
}
