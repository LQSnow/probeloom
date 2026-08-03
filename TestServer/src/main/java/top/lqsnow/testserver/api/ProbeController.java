package top.lqsnow.testserver.api;

import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatusCode;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestMethod;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.time.Instant;
import java.net.URI;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.stream.Collectors;

@RestController
@RequestMapping("/api/v1")
public class ProbeController {

    @GetMapping("/health")
    public HealthResponse health() {
        return new HealthResponse("ok", "ProbeLoom TestServer", Instant.now());
    }

    @RequestMapping(
            value = {"/echo/{resourceId}", "/diagnostics/echo/{resourceId}"},
            method = {
                    RequestMethod.GET,
                    RequestMethod.POST,
                    RequestMethod.PUT,
                    RequestMethod.PATCH,
                    RequestMethod.DELETE,
                    RequestMethod.HEAD,
                    RequestMethod.OPTIONS
            })
    public EchoResponse echo(
            @PathVariable String resourceId,
            @RequestBody(required = false) String body,
            HttpServletRequest request) {
        var query = request.getParameterMap().entrySet().stream()
                .collect(Collectors.toMap(
                        Map.Entry::getKey,
                        entry -> List.of(entry.getValue()),
                        (left, right) -> right,
                        LinkedHashMap::new));
        Map<String, List<String>> headers = Collections.list(request.getHeaderNames()).stream()
                .collect(Collectors.toMap(
                        name -> name,
                        name -> List.copyOf(Collections.list(request.getHeaders(name))),
                        (left, right) -> right,
                        () -> new TreeMap<>(String.CASE_INSENSITIVE_ORDER)));

        return new EchoResponse(
                request.getMethod(),
                request.getRequestURI(),
                resourceId,
                query,
                headers,
                body == null ? "" : body,
                Instant.now());
    }

    @GetMapping("/delay/{milliseconds}")
    public Map<String, Object> delay(@PathVariable long milliseconds) throws InterruptedException {
        if (milliseconds < 0 || milliseconds > 60_000) {
            throw new IllegalArgumentException("milliseconds must be between 0 and 60000");
        }

        Thread.sleep(milliseconds);
        return Map.of(
                "delayedMilliseconds", milliseconds,
                "completedAt", Instant.now().toString());
    }

    @RequestMapping("/status/{statusCode}")
    public ResponseEntity<Map<String, Object>> status(@PathVariable int statusCode) {
        if (statusCode < 200 || statusCode > 599) {
            throw new IllegalArgumentException("statusCode must be between 200 and 599");
        }

        if (statusCode == 204 || statusCode == 304) {
            return ResponseEntity.status(statusCode).build();
        }

        return ResponseEntity.status(HttpStatusCode.valueOf(statusCode))
                .body(Map.of(
                        "status", statusCode,
                        "message", "Intentional response from ProbeLoom TestServer"));
    }

    @GetMapping("/response/json")
    public Map<String, Object> jsonResponse(
            @RequestParam(defaultValue = "ProbeLoom") String name) {
        return Map.of(
                "message", "Hello, " + name,
                "features", List.of("json", "formatting", "unicode: 你好"),
                "nested", Map.of("ready", true, "count", 3));
    }

    @GetMapping(value = "/response/text", produces = MediaType.TEXT_PLAIN_VALUE)
    public String textResponse() {
        return "ProbeLoom plain-text response\nsecond line";
    }

    @GetMapping(value = "/response/html", produces = MediaType.TEXT_HTML_VALUE)
    public String htmlResponse() {
        return """
                <!doctype html>
                <html lang="en">
                <head><title>ProbeLoom Test</title></head>
                <body><h1>HTML response</h1><p>This is test content, not a web UI.</p></body>
                </html>
                """;
    }

    @GetMapping("/response/empty")
    public ResponseEntity<Void> emptyResponse() {
        return ResponseEntity.noContent().build();
    }

    @GetMapping(value = "/response/binary", produces = MediaType.APPLICATION_OCTET_STREAM_VALUE)
    public byte[] binaryResponse() {
        var bytes = new byte[256];
        for (var index = 0; index < bytes.length; index++) {
            bytes[index] = (byte) index;
        }
        return bytes;
    }

    @GetMapping("/response/invalid-json")
    public ResponseEntity<String> invalidJsonResponse() {
        return ResponseEntity.ok()
                .contentType(MediaType.APPLICATION_JSON)
                .body("{\"valid\": false, broken }");
    }

    @GetMapping(value = "/response/large", produces = MediaType.TEXT_PLAIN_VALUE)
    public String largeResponse(@RequestParam(defaultValue = "1024") int kilobytes) {
        if (kilobytes < 1 || kilobytes > 10_240) {
            throw new IllegalArgumentException("kilobytes must be between 1 and 10240");
        }
        return "x".repeat(kilobytes * 1024);
    }

    @GetMapping("/response/headers")
    public ResponseEntity<Map<String, String>> responseHeaders(
            @RequestHeader(value = "X-Probe-Request", defaultValue = "not-provided") String requestMarker) {
        return ResponseEntity.ok()
                .header("X-Probe-Server", "TestServer")
                .header("X-Probe-Echo", requestMarker)
                .header(HttpHeaders.CACHE_CONTROL, "no-store")
                .body(Map.of("receivedMarker", requestMarker));
    }

    @GetMapping("/redirect/start")
    public ResponseEntity<Void> redirectStart() {
        return ResponseEntity.status(302)
                .location(URI.create("/api/v1/redirect/step"))
                .build();
    }

    @GetMapping("/redirect/step")
    public ResponseEntity<Void> redirectStep() {
        return ResponseEntity.status(307)
                .location(URI.create("/api/v1/redirect/final"))
                .build();
    }

    @GetMapping("/redirect/final")
    public Map<String, Object> redirectFinal() {
        return Map.of("redirected", true, "completedAt", Instant.now().toString());
    }

    @GetMapping("/redirect/loop-a")
    public ResponseEntity<Void> redirectLoopA() {
        return ResponseEntity.status(302)
                .location(URI.create("/api/v1/redirect/loop-b"))
                .build();
    }

    @GetMapping("/redirect/loop-b")
    public ResponseEntity<Void> redirectLoopB() {
        return ResponseEntity.status(302)
                .location(URI.create("/api/v1/redirect/loop-a"))
                .build();
    }

    public record HealthResponse(String status, String service, Instant time) {
    }

    public record EchoResponse(
            String method,
            String path,
            String resourceId,
            Map<String, List<String>> query,
            Map<String, List<String>> headers,
            String body,
            Instant receivedAt) {
    }
}
