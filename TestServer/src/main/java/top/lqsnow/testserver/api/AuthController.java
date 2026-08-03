package top.lqsnow.testserver.api;

import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.time.Instant;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

@RestController
@RequestMapping("/api/v1/auth")
public class AuthController {

    private final Map<String, Session> accessSessions = new ConcurrentHashMap<>();
    private final Map<String, Session> refreshSessions = new ConcurrentHashMap<>();

    @PostMapping("/login")
    public ResponseEntity<?> login(@RequestBody LoginRequest request) {
        if (!"developer".equals(request.username()) || !"probe".equals(request.password())) {
            return unauthorized("Invalid username or password");
        }

        var seconds = request.expiresInSeconds() == null ? 120 : request.expiresInSeconds();
        if (seconds < 0 || seconds > 86_400) {
            throw new IllegalArgumentException("expiresInSeconds must be between 0 and 86400");
        }
        return ResponseEntity.ok(issue(seconds));
    }

    @PostMapping("/refresh")
    public ResponseEntity<?> refresh(@RequestBody RefreshRequest request) {
        var session = refreshSessions.remove(request.refreshToken());
        if (session == null) {
            return unauthorized("Refresh token is invalid or has already been used");
        }
        accessSessions.remove(session.accessToken());
        return ResponseEntity.ok(issue(120));
    }

    @GetMapping("/protected")
    public ResponseEntity<?> protectedResource(
            @RequestHeader(value = HttpHeaders.AUTHORIZATION, required = false) String authorization) {
        if (authorization == null || !authorization.startsWith("Bearer ")) {
            return unauthorized("A Bearer token is required");
        }

        var token = authorization.substring("Bearer ".length());
        var session = accessSessions.get(token);
        if (session == null) {
            return unauthorized("Access token is invalid");
        }
        if (!session.expiresAt().isAfter(Instant.now())) {
            return unauthorized("Access token has expired");
        }

        return ResponseEntity.ok(Map.of(
                "authenticated", true,
                "subject", "developer",
                "expiresAt", session.expiresAt().toString()));
    }

    private TokenResponse issue(long expiresInSeconds) {
        var access = "access-" + UUID.randomUUID();
        var refresh = "refresh-" + UUID.randomUUID();
        var session = new Session(access, refresh, Instant.now().plusSeconds(expiresInSeconds));
        accessSessions.put(access, session);
        refreshSessions.put(refresh, session);
        return new TokenResponse(access, refresh, expiresInSeconds, "Bearer");
    }

    private static ResponseEntity<Map<String, Object>> unauthorized(String detail) {
        return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body(Map.of(
                "status", 401,
                "title", "Authentication failed",
                "detail", detail));
    }

    public record LoginRequest(String username, String password, Long expiresInSeconds) {
    }

    public record RefreshRequest(String refreshToken) {
    }

    public record TokenResponse(
            String accessToken,
            String refreshToken,
            long expiresIn,
            String tokenType) {
    }

    private record Session(String accessToken, String refreshToken, Instant expiresAt) {
    }
}
