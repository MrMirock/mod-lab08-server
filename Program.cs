using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Globalization;

namespace Lab08_CMO
{
    public class RequestEventArgs : EventArgs
    {
        public int Id { get; set; }
    }

    public class Server
    {
        private int channels;
        private double serviceRate;
        private Random rand = new Random();

        private bool[] busy;
        private Thread[] threads;

        public int TotalRequests { get; private set; }
        public int Processed { get; private set; }
        public int Rejected { get; private set; }

        // Для экспериментальной вероятности простоя P0
        private int idleChecks;
        private int idleDetected;
        private object statLock = new object();
        private bool monitoring = true;

        public Server(int n, double mu)
        {
            channels = n;
            serviceRate = mu;
            busy = new bool[n];
            threads = new Thread[n];

            Thread monitor = new Thread(MonitorIdle);
            monitor.IsBackground = true;
            monitor.Start();
        }

        private void MonitorIdle()
        {
            while (monitoring)
            {
                Thread.Sleep(100);
                bool allFree = true;
                lock (busy)
                {
                    for (int i = 0; i < channels; i++)
                        if (busy[i]) { allFree = false; break; }
                }
                lock (statLock)
                {
                    idleChecks++;
                    if (allFree) idleDetected++;
                }
            }
        }

        private void ServeRequest(object arg)
        {
            double serviceTime = -Math.Log(1.0 - rand.NextDouble()) / serviceRate;
            Thread.Sleep((int)(serviceTime * 1000));

            lock (busy)
            {
                for (int i = 0; i < channels; i++)
                    if (threads[i] == Thread.CurrentThread)
                    {
                        busy[i] = false;
                        threads[i] = null;
                        break;
                    }
            }
        }

        public void HandleRequest(object sender, RequestEventArgs e)
        {
            TotalRequests++;

            lock (busy)
            {
                for (int i = 0; i < channels; i++)
                {
                    if (!busy[i])
                    {
                        busy[i] = true;
                        threads[i] = new Thread(ServeRequest);
                        threads[i].Start(e.Id);
                        Processed++;
                        return;
                    }
                }
                Rejected++;
            }
        }

        public void Stop()
        {
            monitoring = false;
            bool anyBusy;
            do
            {
                anyBusy = false;
                lock (busy)
                {
                    for (int i = 0; i < channels; i++)
                        if (busy[i]) { anyBusy = true; break; }
                }
                if (anyBusy) Thread.Sleep(20);
            } while (anyBusy);
        }

        public double GetIdleProbability()
        {
            lock (statLock)
            {
                if (idleChecks == 0) return 0;
                return (double)idleDetected / idleChecks;
            }
        }
    }

    public class Client
    {
        public event EventHandler<RequestEventArgs> RequestArrived;

        public Client(Server server)
        {
            this.RequestArrived += server.HandleRequest;
        }

        public void GenerateRequest(int id)
        {
            RequestArrived?.Invoke(this, new RequestEventArgs { Id = id });
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("Лабораторная работа №8 – Многоканальная СМО с отказами");

            const int n = 5; 
            const double mu = 10.0;
            const double duration = 60.0;

            double[] lambdas = { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20 };

            List<ExperimentData> results = new List<ExperimentData>();

            foreach (double lambda in lambdas)
            {
                Console.WriteLine($"\n--- Эксперимент: λ = {lambda} заявок/сек ---");
                ExperimentData data = RunExperiment(n, mu, lambda, duration);
                results.Add(data);
                PrintResult(data);
            }

            SaveResultsToCSV(results, n, mu);
            SaveResultsToTxt(results, n, mu);

            Console.WriteLine("\nГотово! Результаты сохранены в results.csv и results.txt");
            Console.WriteLine("Откройте results.csv и постройте графики.");
            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        static ExperimentData RunExperiment(int n, double mu, double lambda, double durationSec)
        {
            Server server = new Server(n, mu);
            Client client = new Client(server);
            Random rand = new Random();

            DateTime endTime = DateTime.Now.AddSeconds(durationSec);
            int requestId = 0;

            while (DateTime.Now < endTime)
            {
                double interval = -Math.Log(1.0 - rand.NextDouble()) / lambda;
                int ms = (int)(interval * 1000);
                if (ms > 0) Thread.Sleep(ms);
                if (DateTime.Now >= endTime) break;

                client.GenerateRequest(++requestId);
            }

            server.Stop();
            double realTime = (DateTime.Now - endTime.AddSeconds(durationSec)).TotalSeconds;

            double expP0 = server.GetIdleProbability();
            double expPout = (double)server.Rejected / server.TotalRequests;
            double expQ = (double)server.Processed / server.TotalRequests;
            double expA = server.Processed / realTime;
            double expAvgBusy = expA / mu;

            double rho = lambda / mu;
            double sum = 0;
            double fact = 1;
            for (int i = 0; i <= n; i++)
            {
                if (i > 0) fact *= i;
                sum += Math.Pow(rho, i) / fact;
            }
            double theorP0 = 1.0 / sum;                                   // формула (5.1) – вероятность простоя
            double theorPout = (Math.Pow(rho, n) / fact) * theorP0;       // формула (5.2) – вероятность отказа
            double theorQ = 1 - theorPout;                                // относительная пропускная способность
            double theorA = lambda * theorQ;                              // абсолютная пропускная способность
            double theorAvgBusy = rho * theorQ;                           // среднее число занятых каналов

            return new ExperimentData
            {
                Lambda = lambda,
                ExpP0 = expP0,
                TheorP0 = theorP0,
                ExpPout = expPout,
                TheorPout = theorPout,
                ExpQ = expQ,
                TheorQ = theorQ,
                ExpA = expA,
                TheorA = theorA,
                ExpAvgBusy = expAvgBusy,
                TheorAvgBusy = theorAvgBusy,
                Total = server.TotalRequests,
                Processed = server.Processed,
                Rejected = server.Rejected
            };
        }

        static void PrintResult(ExperimentData d)
        {
            Console.WriteLine($"Заявок: всего = {d.Total}, обслужено = {d.Processed}, отказано = {d.Rejected}");
            Console.WriteLine($"P0 (эксп/теор): {d.ExpP0:F4} / {d.TheorP0:F4}");
            Console.WriteLine($"Pотк (эксп/теор): {d.ExpPout:F4} / {d.TheorPout:F4}");
            Console.WriteLine($"Q (эксп/теор): {d.ExpQ:F4} / {d.TheorQ:F4}");
            Console.WriteLine($"A (эксп/теор): {d.ExpA:F2} / {d.TheorA:F2} заявок/сек");
            Console.WriteLine($"Ср.зан.каналов (эксп/теор): {d.ExpAvgBusy:F3} / {d.TheorAvgBusy:F3}");
        }

        static void SaveResultsToCSV(List<ExperimentData> results, int n, double mu)
        {
            using (StreamWriter csv = new StreamWriter("results.csv"))
            {
                csv.WriteLine("Lambda,ExpP0,TheorP0,ExpPout,TheorPout,ExpQ,TheorQ,ExpA,TheorA,ExpAvgBusy,TheorAvgBusy");
                foreach (var d in results)
                {
                    var invariant = CultureInfo.InvariantCulture;
                    csv.WriteLine($"{d.Lambda.ToString(invariant)},{d.ExpP0.ToString(invariant)},{d.TheorP0.ToString(invariant)},...");
                }
            }
        }

        static void SaveResultsToTxt(List<ExperimentData> results, int n, double mu)
        {
            using (StreamWriter w = new StreamWriter("results.txt"))
            {
                w.WriteLine("Лабораторная работа №8 – Многоканальная СМО с отказами");
                w.WriteLine($"Параметры: число каналов n = {n}, интенсивность обслуживания μ = {mu} заявок/сек\n");
                w.WriteLine("Таблица сравнения экспериментальных и теоретических показателей (формулы лекции №5):\n");
                w.WriteLine("λ\tP0(эксп)\tP0(теор)\tPотк(эксп)\tPотк(теор)\tQ(эксп)\tQ(теор)\tA(эксп)\tA(теор)\tср.зан(эксп)\tср.зан(теор)");
                foreach (var d in results)
                {
                    w.WriteLine($"{d.Lambda:F2}\t{d.ExpP0:F4}\t{d.TheorP0:F4}\t{d.ExpPout:F4}\t{d.TheorPout:F4}\t" +
                                $"{d.ExpQ:F4}\t{d.TheorQ:F4}\t{d.ExpA:F2}\t{d.TheorA:F2}\t{d.ExpAvgBusy:F3}\t{d.TheorAvgBusy:F3}");
                }
                w.WriteLine("\nДополнительная статистика:");
                w.WriteLine("λ\tВсего заявок\tОбслужено\tОтказано");
                foreach (var d in results)
                    w.WriteLine($"{d.Lambda:F2}\t{d.Total}\t{d.Processed}\t{d.Rejected}");
            }
        }
    }

    public class ExperimentData
    {
        public double Lambda { get; set; }
        public double ExpP0 { get; set; }
        public double TheorP0 { get; set; }
        public double ExpPout { get; set; }
        public double TheorPout { get; set; }
        public double ExpQ { get; set; }
        public double TheorQ { get; set; }
        public double ExpA { get; set; }
        public double TheorA { get; set; }
        public double ExpAvgBusy { get; set; }
        public double TheorAvgBusy { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Rejected { get; set; }
    }
}