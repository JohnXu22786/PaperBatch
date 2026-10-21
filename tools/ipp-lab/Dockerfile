FROM ubuntu:24.04@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc AS build

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/tmp \
    TMPDIR=/tmp

WORKDIR /opt/paperbatch
COPY tools/ipp-lab/build-packages.lock tools/ipp-lab/runtime-packages.lock tools/ipp-lab/create_license_inventory.py tools/ipp-lab/
COPY tools/ipp-lab/ubuntu-snapshot.sources /etc/apt/sources.list.d/ubuntu.sources

RUN dpkg-query -W -f='${binary:Package}=${Version}\n' | sort > /tmp/base-packages.lock \
    && apt-get -o Acquire::https::snapshot.ubuntu.com::Verify-Peer=false update \
    && xargs -r apt-get -o Acquire::https::snapshot.ubuntu.com::Verify-Peer=false install --yes --no-install-recommends < tools/ipp-lab/build-packages.lock \
    && dpkg-query -W -f='${binary:Package}=${Version}\n' | sort > /tmp/installed-packages.lock \
    && cat /tmp/base-packages.lock tools/ipp-lab/build-packages.lock | sort -u > /tmp/expected-packages.lock \
    && diff -u /tmp/expected-packages.lock /tmp/installed-packages.lock \
    && dpkg-query -W -f='${binary:Package}\t${Version}\n' > /tmp/build-packages.tsv \
    && python3 tools/ipp-lab/create_license_inventory.py /tmp/build-license-inventory.json \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

ADD --checksum=sha256:f51b9edd631db1830b967101b51f0045c8c239ae799dff89f0399f3e47a95c02 https://github.com/OpenPrinting/cups/archive/refs/tags/v2.4.10.tar.gz /tmp/cups.tar.gz

RUN mkdir /tmp/cups \
    && tar -xzf /tmp/cups.tar.gz --strip-components=1 -C /tmp/cups \
    && cd /tmp/cups \
    && ./configure --prefix=/opt/cups --with-dnssd=no --with-tls=no --with-pam-module=no \
    && make -j2 -C cups \
    && make -j2 -C tools ippeveprinter-static ipptool-static \
    && tools/ippeveprinter-static --version | grep -Fx 'CUPS v2.4.10' \
    && tools/ipptool-static --version | grep -Fx 'CUPS v2.4.10' \
    && mkdir -p /opt/cups-build \
    && cp tools/ippeveprinter-static /opt/cups-build/ippeveprinter \
    && cp tools/ipptool-static /opt/cups-build/ipptool

FROM ubuntu:24.04@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/tmp \
    TMPDIR=/tmp

WORKDIR /opt/paperbatch
COPY tools/ipp-lab/runtime-packages.lock tools/ipp-lab/runtime-packages.lock
COPY tools/ipp-lab/ubuntu-snapshot.sources /etc/apt/sources.list.d/ubuntu.sources

RUN dpkg-query -W -f='${binary:Package}=${Version}\n' | sort > /tmp/base-packages.lock \
    && apt-get -o Acquire::https::snapshot.ubuntu.com::Verify-Peer=false update \
    && xargs -r apt-get -o Acquire::https::snapshot.ubuntu.com::Verify-Peer=false install --yes --no-install-recommends < tools/ipp-lab/runtime-packages.lock \
    && dpkg-query -W -f='${binary:Package}=${Version}\n' | sort > /tmp/installed-packages.lock \
    && cat /tmp/base-packages.lock tools/ipp-lab/runtime-packages.lock | sort -u > /tmp/expected-packages.lock \
    && diff -u /tmp/expected-packages.lock /tmp/installed-packages.lock \
    && mkdir -p docs/cups \
    && dpkg-query -W -f='${binary:Package}\t${Version}\n' > docs/runtime-packages.tsv \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /opt/cups-build/ /usr/local/bin/
COPY --from=build /tmp/cups/LICENSE /opt/paperbatch/docs/cups/LICENSE
COPY --from=build /tmp/cups/NOTICE /opt/paperbatch/docs/cups/NOTICE
COPY --from=build /tmp/cups/README.md /opt/paperbatch/docs/cups/README.md
COPY --from=build /tmp/build-packages.tsv /opt/paperbatch/docs/build-packages.tsv
COPY --from=build /tmp/build-license-inventory.json /opt/paperbatch/docs/build-license-inventory.json

COPY tools/ipp_fault_lab.py tools/ipp_fault_lab.py
COPY tools/ipp-lab/ tools/ipp-lab/
COPY tests/test_ipp_fault_lab.py tests/test_ipp_fault_lab.py

RUN python3 tools/ipp-lab/create_license_inventory.py docs/runtime-license-inventory.json \
    && chmod -R a+rX /opt/paperbatch /usr/local/bin/ippeveprinter /usr/local/bin/ipptool

USER 10001:10001

CMD ["python3", "tools/ipp_fault_lab.py", "run", "--scenario", "all"]
