# Pinned TDS test support

The `upstream` subtree is the TDS, TDS.EndPoint and TDS.Servers
test tooling from SqlClient 6.0.2, commit
`b16dec0a5622fd5b3d5311191bac4cafadc43e60`. Its source archive SHA-256 is
`f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870`.
The parent `../upstream/LICENSE` is the retained MIT license.

`Tds.TestSupport.csproj` combines those test sources in one test-only
assembly. It uses the Unix SSPI stub on every host: these tests use synthetic SQL
authentication and never invoke OS integrated authentication. No production
project references this assembly or ships its synthetic certificate. First-party
loopback fixtures and assertions live in the ordinary test project and retain
the repository analyzers.

The only diagnostic exceptions are CA2022 for original token reads and
SYSLIB0057 for the original synthetic-certificate constructor. Independent review
traced `TDSStream.Read` accumulating socket bytes, then `TDSMessage` constructing
the complete-message MemoryStream before token inflation. These token reads do
not receive partial network reads. The helper does not generally validate
malformed token lengths within complete frames; it is trusted loopback test
support, not a production hostile-input parser. First-party tests cover valid
fragmented messages separately.

One separate correctness patch changes `TDSPacketHeader.Inflate` to finish the
fixed eight-byte header using its existing offset and fail with
`EndOfStreamException` on a zero-byte read. Previously a short header read
returned false, and the next `TDSStream.ReadNextHeader` allocation discarded the
partial bytes. First-party one/three-byte fragmented-frame and zero/one/seven-byte
EOF regressions cover this boundary. This repair is not a diagnostic suppression
and does not broaden the helper's malformed-token validation claims.
