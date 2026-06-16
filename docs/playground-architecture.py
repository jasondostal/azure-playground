#!/usr/bin/env python3
"""Generates docs/playground-architecture.png — the azure-playground topology as an
Azure-reference-style diagram (mingrammer `diagrams` + Graphviz, official Azure icons).

    pip install diagrams   # needs system graphviz (`dot`)
    python docs/playground-architecture.py   # run from repo root
"""
from diagrams import Diagram, Cluster, Edge
from diagrams.azure.general import Browser, Usericon
from diagrams.azure.web import AppServices
from diagrams.azure.compute import FunctionApps
from diagrams.azure.database import SQLDatabases, CosmosDb
from diagrams.azure.integration import ServiceBus, EventGridTopics
from diagrams.azure.monitor import ApplicationInsights

graph_attr = {
    "dpi": "160",
    "bgcolor": "white",
    "pad": "0.6",
    "nodesep": "0.55",
    "ranksep": "1.1",
    "fontname": "Helvetica",
    "fontsize": "20",
    "splines": "spline",
}
node_attr = {"fontname": "Helvetica", "fontsize": "13"}
edge_attr = {"fontname": "Helvetica", "fontsize": "12", "color": "#555555"}

# exhibit edge colors
E1 = "#2a9d5a"   # green  — Exhibit 1 (latency)
E2 = "#c0392b"   # red    — Exhibit 2 (Service Bus anti-pattern)
E3 = "#8e44ad"   # purple — Exhibit 3 (integration)

with Diagram(
    "azure-playground",
    filename="docs/playground-architecture",
    outformat="png",
    show=False,
    direction="LR",
    graph_attr=graph_attr,
    node_attr=node_attr,
    edge_attr=edge_attr,
):
    client = Browser("Browser / curl")

    with Cluster("rg-pg-playground"):
        with Cluster("App Service Plan (B1)"):
            web = AppServices("Web app\n(Easy Auth — owner only)")
            api = AppServices("API service\n(shared-secret gated)")

        sb = ServiceBus("Service Bus (Standard)\nqueues + topic")
        egt = EventGridTopics("Event Grid topic")

        with Cluster("Data"):
            sql = SQLDatabases("Azure SQL")
            cosmos = CosmosDb("Cosmos DB")

        ai = ApplicationInsights("App Insights")

    with Cluster("rg-pg-playground-fn"):
        fn = FunctionApps("Functions\n(.NET 9 isolated)")

    client >> Edge(color="#333333") >> web

    # Exhibit 1 — reveal latency: direct vs API-fronted
    web >> Edge(color=E1, label="#1 direct") >> sql
    web >> Edge(color=E1) >> cosmos
    web >> Edge(color=E1, label="#1 via API") >> api
    api >> Edge(color=E1) >> sql
    api >> Edge(color=E1) >> cosmos

    # Exhibit 2 — Service Bus for synchronous reads (anti-pattern)
    web >> Edge(color=E2, label="#2 request/reply") >> sb >> Edge(color=E2) >> api

    # Exhibit 3 — integration tier ("nervous system")
    client >> Edge(color=E3, label="#3 webhook") >> fn
    egt >> Edge(color=E3, label="#3 event") >> fn
    fn >> Edge(color=E3, label="#3 enqueue") >> sb
    cosmos >> Edge(color=E3, style="dashed", label="#3 change feed") >> fn
    fn >> Edge(color=E3, label="#3 items") >> cosmos
    fn >> Edge(color=E3, label="#3 adapter") >> api
    fn >> Edge(color="#888888", style="dotted") >> ai
