// Fixture for scan-fe's jQuery strategy (spec section 9): "1 file jQuery ghép URL động không parse được".
// buildOrderEndpoint(orderId) is a function call, not a string literal/template — the URL has no recognizable
// path structure at all, so FrontendUrlNormalizer must fail this into diagnostics.json instead of guessing.
function cancelOrderLegacy(orderId) {
  var endpoint = buildOrderEndpoint(orderId);
  $.ajax({
    url: endpoint,
    type: 'DELETE'
  });
}
