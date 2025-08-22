// <copyright file="AlternatorLiveNodes.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.Runtime.Endpoints;

    public class AlternatorLiveNodes
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly string alternatorScheme;
        private readonly int alternatorPort;
        private readonly ReaderWriterLockSlim liveNodesLock = new ();
        private readonly List<Uri> initialNodes;
        private readonly string rack;
        private readonly string datacenter;
        private List<Uri> liveNodes;
        private int nextLiveNodeIndex;
        private bool started;

        public AlternatorLiveNodes(Uri liveNode, string datacenter, string rack)
            : this(new List<Uri> { liveNode }, liveNode.Scheme, liveNode.Port, datacenter, rack)
        {
        }

        public AlternatorLiveNodes(List<Uri> nodes, string scheme, int port, string datacenter, string rack)
        {
            if (nodes == null || nodes.Count == 0)
            {
                throw new SystemException("liveNodes cannot be null or empty");
            }

            this.initialNodes = nodes;
            this.alternatorScheme = scheme;
            this.alternatorPort = port;
            this.rack = rack;
            this.datacenter = datacenter;
            this.liveNodes = new List<Uri>();
            foreach (var node in this.initialNodes)
            {
                this.liveNodes.Add(node);
            }
        }

        public Task Start(CancellationToken cancellationToken)
        {
            if (this.started)
            {
                return Task.CompletedTask;
            }

            this.Validate();

            Task.Run(
                () =>
            {
                this.UpdateCycle(cancellationToken);
                return Task.CompletedTask;
            }, cancellationToken);
            this.started = true;
            return Task.CompletedTask;
        }

        public void Validate()
        {
            try
            {
                // Make sure that `alternatorScheme` and `alternatorPort` are correct values
                this.HostToUri("1.1.1.1");
            }
            catch (UriFormatException e)
            {
                throw new ValidationError("failed to validate configuration", e);
            }
        }

        public Uri NextAsUri()
        {
            var nodes = this.GetLiveNodes();
            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("No live nodes available");
            }

            return nodes[Math.Abs(Interlocked.Increment(ref this.nextLiveNodeIndex) % nodes.Count)];
        }

        public void CheckIfRackAndDatacenterSetCorrectly()
        {
            if (string.IsNullOrEmpty(this.rack) && string.IsNullOrEmpty(this.datacenter))
            {
                return;
            }

            try
            {
                var nodes = this.GetNodes(this.NextAsLocalNodesUri());
                if (nodes.Count == 0)
                {
                    throw new ValidationError("node returned empty list, datacenter or rack are set incorrectly");
                }
            }
            catch (IOException e)
            {
                throw new FailedToCheck("failed to read list of nodes from the node", e);
            }
        }

        public bool CheckIfRackDatacenterFeatureIsSupported()
        {
            var uri = this.NextAsUri("/localnodes", string.Empty);
            Uri fakeRackUrl;
            try
            {
                fakeRackUrl = new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.Query}&rack=fakeRack");
            }
            catch (UriFormatException e)
            {
                // Should not ever happen
                throw new FailedToCheck("Invalid Uri: " + uri, e);
            }

            try
            {
                var hostsWithFakeRack = this.GetNodes(fakeRackUrl);
                var hostsWithoutRack = this.GetNodes(uri);
                if (hostsWithoutRack.Count == 0)
                {
                    // This should not normally happen.
                    // If list of nodes is empty, it is impossible to conclude if it supports rack/datacenter filtering or not.
                    throw new FailedToCheck($"host {uri} returned empty list");
                }

                // When rack filtering is not supported server returns same nodes.
                return hostsWithFakeRack.Count != hostsWithoutRack.Count;
            }
            catch (IOException e)
            {
                throw new FailedToCheck("failed to read list of nodes from the node", e);
            }
        }

        private static string StreamToString(Stream stream)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private void UpdateCycle(CancellationToken cancellationToken)
        {
            Logger.Debug("AlternatorLiveNodes thread started");
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        this.UpdateLiveNodes();
                    }
                    catch (IOException e)
                    {
                        Logger.Error(e, "AlternatorLiveNodes failed to sync nodes list: %");
                    }

                    try
                    {
                        Thread.Sleep(1000);
                    }
                    catch (ThreadInterruptedException)
                    {
                        Logger.Info("AlternatorLiveNodes thread interrupted and stopping");
                        return;
                    }
                }
            }
            finally
            {
                Logger.Info("AlternatorLiveNodes thread stopped");
            }
        }

        private Uri HostToUri(string host)
        {
            return new Uri($"{this.alternatorScheme}://{host}:{this.alternatorPort}");
        }

        private List<Uri> GetLiveNodes()
        {
            this.liveNodesLock.EnterReadLock();
            try
            {
                return this.liveNodes.ToList();
            }
            finally
            {
                this.liveNodesLock.ExitReadLock();
            }
        }

        private void SetLiveNodes(List<Uri> nodes)
        {
            this.liveNodesLock.EnterWriteLock();
            this.liveNodes = nodes;
            this.liveNodesLock.ExitWriteLock();
        }

        private Uri NextAsUri(string path, string query)
        {
            Uri uri = this.NextAsUri();
            if (string.IsNullOrEmpty(query))
            {
                return new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}{path}");
            }

            return new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}{path}?{query}");
        }

        private void UpdateLiveNodes()
        {
            var newHosts = this.GetNodes(this.NextAsLocalNodesUri());
            if (newHosts.Count == 0)
            {
                return;
            }

            this.SetLiveNodes(newHosts);
            Logger.Info($"Updated hosts to {this.liveNodes}");
        }

        private List<Uri> GetNodes(Uri uri)
        {
            using var client = new HttpClient();
            var response = client.GetAsync(uri).Result;
            if (!response.IsSuccessStatusCode)
            {
                return new List<Uri>();
            }

            var responseBody = StreamToString(response.Content.ReadAsStreamAsync().Result);

            // response looks like: ["127.0.0.2","127.0.0.3","127.0.0.1"]
            responseBody = responseBody.Trim();
            responseBody = responseBody.Substring(1, responseBody.Length - 2);
            var list = responseBody.Split(',');
            var newHosts = new List<Uri>();
            foreach (var host in list)
            {
                if (string.IsNullOrEmpty(host))
                {
                    continue;
                }

                var trimmedHost = host.Trim().Substring(1, host.Length - 2);
                try
                {
                    newHosts.Add(this.HostToUri(trimmedHost));
                }
                catch (UriFormatException e)
                {
                    Logger.Error(e, $"Invalid host: {trimmedHost}");
                }
            }

            return newHosts;
        }

        private Uri NextAsLocalNodesUri()
        {
            if (string.IsNullOrEmpty(this.rack) && string.IsNullOrEmpty(this.datacenter))
            {
                return this.NextAsUri("/localnodes", string.Empty);
            }

            var query = string.Empty;
            if (!string.IsNullOrEmpty(this.rack))
            {
                query = "rack=" + this.rack;
            }

            if (string.IsNullOrEmpty(this.datacenter))
            {
                return this.NextAsUri("/localnodes", query);
            }

            if (string.IsNullOrEmpty(query))
            {
                query = $"dc={this.datacenter}";
            }
            else
            {
                query += $"&dc={this.datacenter}";
            }

            return this.NextAsUri("/localnodes", query);
        }
    }
}