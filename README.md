# BLIP-Net
The .NET implementation of the [BLIP protocol](https://github.com/couchbaselabs/BLIP-Cocoa/blob/master/Docs/BLIP%20Protocol.md).  Note that a BLIP compatible server is also needed.  Currently the only one is [Sync Gateway](https://github.com/couchbase/sync_gateway), but this functionality is [not yet merged into master](https://github.com/couchbase/sync_gateway/pull/1086).  Until it is you need to first build from source on the `feature/blipsync` branch and add the following to your sync gateway db config:

```
"unsupported": {
  "replicator_2": true
}
```

After that you can establish a connection via `http(s)://<sync_gateway_url>:<sync_gateway_port>/<db_name>/_blipsync`
