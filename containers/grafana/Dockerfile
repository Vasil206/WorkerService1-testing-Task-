FROM grafana/grafana:10.2.2

COPY ./etc/grafana/provisioning/dashboards /etc/grafana/provisioning/dashboards

COPY ./etc/grafana/provisioning/datasources /etc/grafana/provisioning/datasources

ENV GF_SERVER_ROOT_URL="%(protocol)s://%(domain)s:%(http_port)s/grafana/"

ENV GF_SERVER_SERVE_FROM_SUB_PATH=true