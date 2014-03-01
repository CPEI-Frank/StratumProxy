using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Stratum;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace StratumProxy
{
	class Program
	{
		static TcpListener listener = new TcpListener(IPAddress.Any, 4502);

		const int BUFFER_SIZE = 4096;

		static void Main(string[] args)
		{
			listener.Start();
			new Task(() =>
			{
				// Accept clients.
				while (true)
				{
					TcpClient client = listener.AcceptTcpClient();
					new Task(() => MinerClient(client)).Start();
				}
			}).Start();
			Console.WriteLine("Server listening on port 4502.  Press enter to exit.");
			Console.ReadLine();
			listener.Stop();
		}

		static void ConnectToPool(int id, ref StreamReader serverReader, ref StreamWriter serverWriter)
		{
			// es adatbazis alapjan valahova konnectaltatni...
			TcpClient server = new TcpClient("elbandi.net", 80);
			NetworkStream serverStream;
			serverStream = server.GetStream();
			serverWriter = new StreamWriter(serverStream);
			serverReader = new StreamReader(serverStream);
		}

		static void MinerClient(TcpClient client)
		{
			try
			{
				// Handle this client.
				NetworkStream clientStream = client.GetStream();
				Thread.CurrentThread.Name = "Main - " + client.Client.RemoteEndPoint;
				StreamReader clientReader = new StreamReader(clientStream);
				StreamWriter clientWriter = new StreamWriter(clientStream);
				ManualResetEvent refreshWait = new ManualResetEvent(false);
				int userid = -1;
				StreamWriter serverWriter = null;
				StreamReader serverReader = null;
				new Task(() =>
				{
					string clientString;
					Thread.CurrentThread.Name = "Client - " + client.Client.RemoteEndPoint;
					while (true)
					{
						try
						{
							clientString = clientReader.ReadLine();

							Request r = JsonConvert.DeserializeObject<Request>(clientString);
							if (r.Method.Equals("mining.subscribe"))
							{ // nincs server, ezert meg csak visszakuldunk valamit...
								clientWriter.WriteLine("{\"error\": null, \"id\": 2, \"result\": true}");
								continue;
							}
							if (r.Method.Equals("mining.authorize"))
							{
								// itt le kene ellenorzni ki/mi o
								userid = 0;

								ConnectToPool(userid, ref serverReader, ref serverWriter);
								//be kell jelentkezni + autholni magunkat a poolba
								refreshWait.Set();
								clientWriter.WriteLine(string.Format("{{\"error\": null, \"id\": {0}, \"result\": true}}", r.Id));
								// ha oke, akkor serverhez csatlakozas
								continue;
							}
							if (serverReader == null)
							{ // nem autholt, dobjuk
								break;
							}
							if (r.Method.Equals("mining.submit"))
							{
								// jee user kuldott valami sharet
								// TODO: le kene menteni
							}

							// TODO: itt valami varakozas, ha epp ujracsatlakozas van folyamatban a poolhoz
							serverWriter.WriteLine(clientString);
							serverWriter.Flush();
						}
						catch
						{
							// Socket error or client disconnected - exit loop.  Client will have to reconnect.
							break;
						}
					}
					client.Close();
				}).Start();
				new Task(() =>
				{
					string serverString;
					Thread.CurrentThread.Name = "Server - " + client.Client.RemoteEndPoint;
					refreshWait.WaitOne();
					while (true)
					{
						try
						{
							serverString = serverReader.ReadLine();
							if (serverString == null)
							{
								// server bezarta a kapcsolatot
								break;
							}
							if (serverString.Equals("\"error\""))
							{
								StratumResult r = JsonConvert.DeserializeObject<StratumResult>(serverString);
								// TODO: na le kene konyvelni hogy mi tortent a shareval
							}
							clientWriter.WriteLine(serverString);
							clientWriter.Flush();
						}
						catch
						{
							// Server socket error or disconnect - exit loop.  Client will have to reconnect.
							break;
						}
					}
					//TODO: eleg ezt lezarni?
					client.Close();
				}).Start();
			}
			catch
			{
				// nagy global mindent elkapo kivetel
			}
		}
	}
}
