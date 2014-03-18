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
using MySql.Data.MySqlClient;
using System.Configuration;
using System.Collections;

namespace StratumProxy
{
	class Program
	{
		static TcpListener listener = new TcpListener(IPAddress.Any, 4502);
		static int serverid = 1;
		static string startnotify;
		static string extranonce1;
		static int extranonce2 = 0;
		static int extranonce2_size;
		static ulong difficulty = 0;
		static string miningnotify = null;
		static StreamWriter serverWriter = null;
		static StreamReader serverReader = null;

		// TODO: lockolast a threadek kozott
		static List<MinerClientData> miners = new List<MinerClientData>();
		static Dictionary<int, MinerSubmit> submitids = new Dictionary<int, MinerSubmit>();

		const int BUFFER_SIZE = 4096;


		static void Main(string[] args)
		{
			ConnectToPool();
			new Task(() =>
			{
				string serverString;
				Thread.CurrentThread.Name = "Server";
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
						Console.WriteLine("S>: " + serverString);
						serverString = serverString.Replace("id\": null", "id\": 0"); // fixme: json deserialize null-t is!!
						if (serverString.Contains("\"method\":"))
						{
							// ez egy method keres
							Request r = JsonConvert.DeserializeObject<Request>(serverString);
							if (r.Method.Equals("mining.set_difficulty"))
							{
								difficulty = (ulong)r.Params[0];
								// mindenkinek uzenni
								ICollection myminers = miners;
								lock (myminers.SyncRoot)
								{
									foreach (MinerClientData m in myminers)
									{
										m.clientWriter.WriteLine(serverString);
									}
								}
							}
							else if (r.Method.Equals("mining.notify"))
							{ // notify mindenkinek uzenni
								miningnotify = serverString;
								ICollection myminers = miners;
								lock (myminers.SyncRoot)
								{
									foreach (MinerClientData m in myminers)
									{
										m.clientWriter.WriteLine(serverString);
									}
								}
							}
						}
						else
						{
							StratumResult r = JsonConvert.DeserializeObject<StratumResult>(serverString);
							if (r.Error != null)
							{
								// TODO: ezt hogy kell triggerelni?
							}
							else
							{
								if (r.Id == 1) // ez a subscriptbe valasz
								{
									// {"error": null, "id": 1, "result": [["mining.notify", "ae6812eb4cd7735a302a8a9dd95cf71f"], "f80020c7", 4]}
									JArray result = (JArray)r.Result;
									startnotify = result[0].ToString(Formatting.None);
									extranonce1 = (string)result[1];
									extranonce2_size = (int)result[2];

									// autholjunk be
									serverWriter.WriteLine(string.Format("{{\"params\": [\"{1}\", \"{2}\"], \"id\": {0}, \"method\": \"mining.authorize\"}}", serverid = 2, "elbandi.test", "x"));
								}
								else if (r.Id == 2) // ez az auth valasz
								{
									if ((bool)r.Result != true) throw new Exception("Failed to auth to pool");
								}
								else //ez egy reponse valaszt
								{
									int id = (int)r.Id;
									if (submitids.ContainsKey(id))
									{
										MinerSubmit m = submitids[id];
										r.Id = m.id; // visszairjuk a regi id-t
										// TODO: na le kene konyvelni hogy mi tortent a shareval
										if (m.miner.client.Connected)
										{
											string o = JsonConvert.SerializeObject(r, Formatting.None);
											m.miner.clientWriter.WriteLine(o);
										}
										submitids.Remove(id);
									}
								}
							}
						}
					}
					catch (Exception e)
					{
						// Server socket error or disconnect - exit loop.  Client will have to reconnect.
						Console.WriteLine("ERROR: " + e.Message);
						break;
					}
				}
				// mindenkit ki kell dobni a francba, es kilepni valahogy az egeszbol
				foreach (MinerClientData m in miners)
				{
					m.client.Close();
				}
			}).Start();

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

		static void ConnectToPool()
		{
			NetworkStream serverStream;
			TcpClient server = new TcpClient("84.2.34.69", 3333);
			serverStream = server.GetStream();
			serverWriter = new StreamWriter(serverStream);
			serverReader = new StreamReader(serverStream);
			serverWriter.AutoFlush = true;
			serverWriter.WriteLine(string.Format("{{\"id\": {0}, \"method\": \"mining.subscribe\", \"params\": []}}", serverid = 1));
		}

		static int GetUser(string username, string password)
		{
			using (var conn = new MySqlConnection(ConfigurationManager.ConnectionStrings["mpos"].ConnectionString))
			using (var cmd = conn.CreateCommand())
			{
				conn.Open();
				cmd.CommandText = "SELECT account_id FROM pool_worker WHERE username = ?username AND password = ?password";
				cmd.CommandTimeout = 5;
				cmd.Parameters.AddWithValue("?username", username);
				cmd.Parameters.AddWithValue("?password", password);
				using (var reader = cmd.ExecuteReader())
				{
					while (reader.HasRows && reader.Read())
					{
						return (int)reader[0];
					}
				}
			}
			throw new Exception("No such user");
		}

		static void MinerClient(TcpClient client)
		{
			MinerClientData miner = null;
			try
			{
				NetworkStream clientStream = client.GetStream();
				Thread.CurrentThread.Name = "Client - " + client.Client.RemoteEndPoint;
				miner = new MinerClientData(client, new StreamReader(clientStream), new StreamWriter(clientStream));
				int userid = -1;

				string clientString;
				while (true)
				{
					try
					{
						clientString = miner.clientReader.ReadLine();
						if (clientString == null)
						{
							// client bezarta a kapcsolatot
							break;
						}
						Console.WriteLine("CL>: " + clientString);

						Request r = JsonConvert.DeserializeObject<Request>(clientString);
						if (r.Method.Equals("mining.subscribe"))
						{
							/*
							// FIXME: ossze kell rakni ertelmes valaszt
							string extranonce = string.Format("{0}{1,2:X2}", extranonce1, extranonce2++);
							JArray array = new JArray(startnotify, extranonce, extranonce2_size - 1);
							StratumResult response = new StratumResult(1, array, null);
							miner.clientWriter.WriteLine(response.ToString());
							 */
							miner.extranonce = string.Format("{0,2:X2}", extranonce2++);
							miner.clientWriter.WriteLine(string.Format("{{\"id\": 1, \"result\": [{0},  \"{1}{2}\", {3}], \"error\": null}}", startnotify, extranonce1, miner.extranonce, extranonce2_size - 1));
							if (difficulty > 0)
							{
								miner.clientWriter.WriteLine(string.Format("{{\"params\": [{0}], \"id\": null, \"method\": \"mining.set_difficulty\"}}", difficulty));
							}
							continue;
						}
						if (r.Method.Equals("mining.authorize"))
						{
							if (r.Params.Count < 2)
							{
								break;
							}
							// itt le kene ellenorzni ki/mi o
							userid = GetUser(r.Params[0].ToString(), r.Params[1].ToString());

							miner.clientWriter.WriteLine(string.Format("{{\"error\": null, \"id\": {0}, \"result\": true}}", r.Id));
							if (miningnotify != null)
							{
								miner.clientWriter.WriteLine(miningnotify);
							}
							miners.Add(miner);
							continue;
						}
						if (userid == -1)
						{ // nem autholt, dobjuk
							break;
						}
						if (r.Method.Equals("mining.submit"))
						{
							// jee user kuldott valami sharet
							int oldid = r.Id;
							JArray result = (JArray)r.Params;
							r.Id = serverid++; // uj idt generaltunk.
							result[2] = miner.extranonce + result[2];
							// TODO: le kene menteni dbbe a sharet
							submitids.Add(r.Id, new MinerSubmit { id = oldid, miner = miner });
							string o = JsonConvert.SerializeObject(r, Formatting.None);
							serverWriter.WriteLine(o);
						}
						else
						{
							serverWriter.WriteLine(clientString);
						}
					}
					catch
					{
						// Socket error or client disconnected - exit loop.  Client will have to reconnect.
						break;
					}
				}
				if (miners.Contains(miner))
				{
					miners.Remove(miner);
				}
				client.Close();
			}
			catch
			{
				// nagy global mindent elkapo kivetel
				if (miners.Contains(miner))
				{
					miners.Remove(miner);
				}
			}
		}
		class MinerSubmit
		{
			public MinerClientData miner { get; set; }
			public int id { get; set; }
		}
		class MinerClientData
		{
			public TcpClient client { get; private set; }
			public StreamReader clientReader { get; private set; }
			public StreamWriter clientWriter { get; private set; }
			public string extranonce { get; set; }
			public MinerClientData(TcpClient client, StreamReader clientReader, StreamWriter clientWriter)
			{
				this.client = client;
				this.clientReader = clientReader;
				this.clientWriter = clientWriter;
				this.clientWriter.AutoFlush = true;
			}
		}
	}
}
