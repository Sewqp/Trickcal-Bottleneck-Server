"""Read-only APM regression for an even-player, one-match-request-per-player scenario.
Run the scenario with APM_DETAIL_SAMPLE_RATE=1, then pass its exact APM_RUN_ID.
"""
import argparse
import collections
import json
import urllib.parse
import urllib.request

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--collector", default="http://127.0.0.1:5219")
    parser.add_argument("--service", required=True)
    parser.add_argument("--run", required=True)
    parser.add_argument("--players", type=int, default=20)
    args = parser.parse_args()
    if args.players < 2 or args.players > 200 or args.players % 2:
        parser.error("--players must be an even number from 2 to 200")

    def get(path):
        with urllib.request.urlopen(args.collector.rstrip("/") + path, timeout=15) as response:
            return json.load(response)

    def entries(operation, category):
        query = urllib.parse.urlencode({"server":args.service, "run":args.run, "operation":operation,
                                        "category":category, "minutes":1440, "limit":200})
        page = get("/api/traces?" + query)
        assert not page["hasMore"], "Too many results; choose one isolated scenario run."
        return page["items"]

    requests = entries("MatchRequest", "packet")
    notifications = entries("MatchResult", "background")
    assert len(requests) == len(notifications) == args.players
    assert all(r["outcome"] == "success" for r in requests)
    codes = collections.Counter(r["resultCode"] for r in requests)
    assert codes == {"match_queued":args.players // 2, "match_created":args.players // 2}, codes
    by_trace = {r["traceId"]:r for r in requests}
    linked_requests = set()
    for notification in notifications:
        assert notification["outcome"] == "success" and notification["resultCode"] == "MatchResult"
        source = by_trace[notification["linkedTraceId"]]
        assert notification["traceId"] != source["traceId"], "Notification must have an independent trace."
        assert notification["sessionId"] == source["sessionId"] and notification["userKey"] == source["userKey"]
        assert source["traceId"] not in linked_requests, "Duplicate completion notification."
        linked_requests.add(source["traceId"])
        detail = get("/api/traces/" + notification["traceId"])
        origin = get("/api/traces/" + source["traceId"])
        assert not detail["truncated"] and detail["missingParents"] == 0
        assert any(s["name"] == "response.send" for s in detail["spans"])
        assert any(s["spanId"] == notification["linkedSpanId"] for s in origin["spans"])
    print(json.dumps({"run":args.run, "matchRequests":len(requests), "requestResults":dict(codes),
                      "successfulNotifications":len(notifications), "verifiedOriginalRequestLinks":len(linked_requests),
                      "unknownMatchRequests":0}, indent=2))

if __name__ == "__main__":
    main()
