using System;
using System.Net;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Specialized;
using System.Configuration;
using System.Data;
using System.Web;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using RabbitMQ.Client;

namespace RMQCLI {

    public class Logging {

        public static string GenerateDefaultLogFileName(string BaseFileName) {
            return AppDomain.CurrentDomain.BaseDirectory + "\\" + BaseFileName + "_" + DateTime.Now.Month + "_" + DateTime.Now.Day + "_" + DateTime.Now.Year + ".log";
        }
	
        public static void WriteToLog(string LogPath, string Src, string Message) {
            try {
                using (StreamWriter s = File.AppendText(LogPath))
                {
                    s.WriteLine(DateTime.Now + ";" + Src + ";" + Message);
                }
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
	
        public static void WriteToEventLog(string Source, string Message, System.Diagnostics.EventLogEntryType EntryType) {
            try {
                if (!System.Diagnostics.EventLog.SourceExists(Source)) {
                    System.Diagnostics.EventLog.CreateEventSource(Source, "Application");
                }
                System.Diagnostics.EventLog.WriteEntry(Source, Message, EntryType);
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
    }
	
    public class AppConfig {

        public static int Load(ref Hashtable ht) {
            try {
                // Load application settings from file
                System.Configuration.Configuration xmlConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                System.Configuration.AppSettingsSection appSettings = xmlConfig.AppSettings;
                if (appSettings.Settings.Count != 0) {
                    foreach (string key in appSettings.Settings.AllKeys) {
                        ht.Add(key, appSettings.Settings[key].Value);
		    }
                } else {
                    return 1;
                }
                return 0;
            }
            catch (Exception e) {
                Console.WriteLine("AppConfig.Exception caught\n" + e.Source.ToString() + ": " + e.Message.ToString());
                return 1;
            }
        }
    }

    class TestSSL {
    
        private static bool ValidateSslCerts(object sender,
                                             System.Security.Cryptography.X509Certificates.X509Certificate certificate,
                                             System.Security.Cryptography.X509Certificates.X509Chain chain,
                                             System.Net.Security.SslPolicyErrors sslPolicyErrors) {
            // If the certificate is a valid, signed certificate, return true.
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None) {
                Console.WriteLine("no SSL errors");
                //Console.WriteLine("Server SSL Certificate:\n\n" + certificate);
                Console.WriteLine("Server SSL Certificate: " + certificate.Subject + "\n");
                Console.WriteLine("Certificate Chain:\n");
                foreach (System.Security.Cryptography.X509Certificates.X509ChainElement chain_link in chain.ChainElements) {
                    Console.WriteLine("Certificate:\n" + chain_link.Certificate);
                    //Console.WriteLine("Information:\n" + chain_link.Information);
                    //Console.WriteLine("Status:");
                    //foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus chain_link_status in chain_link.ChainElementStatus) {
                    //    Console.WriteLine(chain_link_status.Status);
                    //}
                }
                return true;
            }
            Console.WriteLine("detected SSL errors");
            // If there are errors in the certificate chain, look at each error to determine the cause.
            if ((sslPolicyErrors & System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0) {
                Console.WriteLine("detected SSL certificate chain errors");
                if (chain != null && chain.ChainStatus != null) {
                    foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus status in chain.ChainStatus) {
                        if ((certificate.Subject == certificate.Issuer) && (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot)) {
                            // Self-signed certificates with an untrusted root are valid. 
                            continue;
                        } else {
                            if (status.Status != System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError) {
                                // If there are any other errors in the certificate chain, the certificate is invalid,
                                // so the method returns false.
                                return false;
                            }
                        }
                    }
                }

                // When processing reaches this line, the only errors in the certificate chain are 
                // untrusted root errors for self-signed certificates. These certificates are valid
                // for default Exchange server installations, so return true.
                return true;
            } else {
                // In all other cases, return false.
                return false;
            }
        }
                
        public static int Main(string[] args) {
            Hashtable cnxSettings = new Hashtable();
            if (AppConfig.Load(ref cnxSettings) > 0) {
                Console.WriteLine( "error occurred while parsing .exe.config.");
            }
            string queueName = cnxSettings["AMQ_QUEUE"].ToString();
            //ServicePointManager.ServerCertificateValidationCallback = ValidateSslCerts;
            try {
                Console.WriteLine("connecting to Active MQ queue {4} over tcp/{1} on {0} with {2}/{3} credentials",
                              cnxSettings["AMQ_HOST"].ToString(), cnxSettings["AMQ_PORT"].ToString(), cnxSettings["AMQ_USER"].ToString(),
                              cnxSettings["AMQ_PASS"].ToString(), cnxSettings["AMQ_QUEUE"].ToString() );
                ConnectionFactory factory = new ConnectionFactory();
                factory.HostName = cnxSettings["AMQ_HOST"].ToString();
                factory.UserName = cnxSettings["AMQ_USER"].ToString();
                factory.Password = cnxSettings["AMQ_PASS"].ToString();
                factory.VirtualHost = cnxSettings["AMQ_VHOST"].ToString();
                factory.Port = Convert.ToInt32(cnxSettings["AMQ_PORT"]);
                SslOption sslopts = new SslOption();
                sslopts.AcceptablePolicyErrors = SslPolicyErrors.RemoteCertificateNameMismatch |
                                                 SslPolicyErrors.RemoteCertificateChainErrors |
                                                 SslPolicyErrors.RemoteCertificateNotAvailable;
                sslopts.Enabled = true;
                sslopts.Version =  System.Security.Authentication.SslProtocols.Tls12;
                sslopts.ServerName = cnxSettings["AMQ_HOSTNAME"].ToString();
                sslopts.CertificateValidationCallback = ValidateSslCerts;
                factory.Ssl = sslopts;
                // AMQP 0-9-1 Protocol Spec: https://www.rabbitmq.com/resources/specs/amqp0-9-1.pdf
                //IProtocol protocol = Protocols.DefaultProtocol;
                IProtocol protocol = Protocols.AMQP_0_9_1;
                IConnection conn = factory.CreateConnection();
                IModel channel = conn.CreateModel();
                channel.QueueDeclarePassive(queueName);
                Console.WriteLine("connected ...");
                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += (model, ea) => {
                    var body = ea.Body;
                    var message = Encoding.UTF8.GetString(body);
                    Console.WriteLine(" * rx: {0}", message);
                };
                // Disable AutoAcknowledgements. This way a broker will keep items in the queue
                channel.BasicConsume(queue: queueName, noAck: true, consumer: consumer);
                Console.WriteLine("Press Enter to terminate this channel.");
                Console.ReadLine();
                conn.Close(200, "Closing the connection");
                Console.WriteLine("closing the connection ...");
            } catch(Exception e) {
                Console.WriteLine(e.Source.ToString() + ": " + e.Message.ToString());
                return 1;
            }
            return 0;
        }
    }
}

