

FROM prom/prometheus:v2.48.0

COPY ./etc/prometheus /etc/prometheus

CMD ["--config.file=/etc/prometheus/prometheus.yml", \
    "--storage.tsdb.path=/prometheus", \
    "--web.console.libraries=/etc/prometheus/console_libraries", \
    "--web.console.templates=/etc/prometheus/consoles", \
    "--storage.tsdb.retention.time=200h", \
    "--web.enable-lifecycle"]