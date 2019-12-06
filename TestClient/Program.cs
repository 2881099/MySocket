using System;

namespace TestClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var client = new ClientSocket();
            client.Error += (sender, e) => {
                Console.WriteLine("[" + DateTime.Now.ToString("MM-dd HH:mm:ss") + "] "
                    + e.Exception.Message + e.Exception.StackTrace);
            };
            client.Receive += (sender, e) => {
                switch (e.Messager.Action)
                {
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
        }
    }
}
