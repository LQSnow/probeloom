package top.lqsnow.testserver;

import com.jayway.jsonpath.JsonPath;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;

import static org.hamcrest.Matchers.hasItem;
import static org.hamcrest.Matchers.startsWith;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.patch;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.header;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

@SpringBootTest
@AutoConfigureMockMvc
class TestServerApplicationTests {

    @Autowired
    private MockMvc mockMvc;

    @Test
    void contextLoads() {
    }

    @Test
    void healthEndpointIsReady() throws Exception {
        mockMvc.perform(get("/api/v1/health"))
                .andExpect(status().isOk())
                .andExpect(content().contentTypeCompatibleWith(MediaType.APPLICATION_JSON))
                .andExpect(jsonPath("$.status").value("ok"))
                .andExpect(jsonPath("$.service").value("ProbeLoom TestServer"));
    }

    @Test
    void echoCapturesMethodPathParametersQueryHeadersAndBody() throws Exception {
        mockMvc.perform(patch("/api/v1/diagnostics/echo/user 42")
                        .queryParam("tag", "first", "second")
                        .header("X-Probe-Test", "header-value")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("""
                                {"name":"Ada"}
                                """))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.method").value("PATCH"))
                .andExpect(jsonPath("$.path").value("/api/v1/diagnostics/echo/user%2042"))
                .andExpect(jsonPath("$.resourceId").value("user 42"))
                .andExpect(jsonPath("$.query.tag", hasItem("first")))
                .andExpect(jsonPath("$.query.tag", hasItem("second")))
                .andExpect(jsonPath("$.headers.X-Probe-Test[0]").value("header-value"))
                .andExpect(jsonPath("$.body").value(startsWith("{\"name\":\"Ada\"}")));
    }

    @Test
    void statusAndEmptyResponsesAreControllable() throws Exception {
        mockMvc.perform(get("/api/v1/status/418"))
                .andExpect(result -> assertEquals(418, result.getResponse().getStatus()))
                .andExpect(jsonPath("$.status").value(418));

        mockMvc.perform(get("/api/v1/response/empty"))
                .andExpect(status().isNoContent())
                .andExpect(content().string(""));
    }

    @Test
    void responseHeadersCanBeInspected() throws Exception {
        mockMvc.perform(get("/api/v1/response/headers")
                        .header("X-Probe-Request", "round-trip"))
                .andExpect(status().isOk())
                .andExpect(header().string("X-Probe-Server", "TestServer"))
                .andExpect(header().string("X-Probe-Echo", "round-trip"))
                .andExpect(jsonPath("$.receivedMarker").value("round-trip"));
    }

    @Test
    void redirectsExposeARepeatableChainAndLoop() throws Exception {
        mockMvc.perform(get("/api/v1/redirect/start"))
                .andExpect(status().isFound())
                .andExpect(header().string("Location", "/api/v1/redirect/step"));
        mockMvc.perform(get("/api/v1/redirect/step"))
                .andExpect(status().isTemporaryRedirect())
                .andExpect(header().string("Location", "/api/v1/redirect/final"));
        mockMvc.perform(get("/api/v1/redirect/final"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.redirected").value(true));
        mockMvc.perform(get("/api/v1/redirect/loop-a"))
                .andExpect(status().isFound())
                .andExpect(header().string("Location", "/api/v1/redirect/loop-b"));
    }

    @Test
    void responseKindsUseExpectedContentTypes() throws Exception {
        mockMvc.perform(get("/api/v1/response/text"))
                .andExpect(status().isOk())
                .andExpect(content().contentTypeCompatibleWith(MediaType.TEXT_PLAIN));

        mockMvc.perform(get("/api/v1/response/html"))
                .andExpect(status().isOk())
                .andExpect(content().contentTypeCompatibleWith(MediaType.TEXT_HTML));

        mockMvc.perform(get("/api/v1/response/binary"))
                .andExpect(status().isOk())
                .andExpect(content().contentTypeCompatibleWith(MediaType.APPLICATION_OCTET_STREAM))
                .andExpect(content().bytes(expectedBinaryResponse()));
    }

    @Test
    void invalidInputsReturnProblemDetails() throws Exception {
        mockMvc.perform(get("/api/v1/delay/60001"))
                .andExpect(status().isBadRequest())
                .andExpect(content().contentTypeCompatibleWith(MediaType.APPLICATION_PROBLEM_JSON))
                .andExpect(jsonPath("$.title").value("Invalid test request"));

        mockMvc.perform(get("/api/v1/response/large").queryParam("kilobytes", "0"))
                .andExpect(status().isBadRequest());
    }

    @Test
    void largeResponseUsesRequestedSize() throws Exception {
        mockMvc.perform(get("/api/v1/response/large").queryParam("kilobytes", "2"))
                .andExpect(status().isOk())
                .andExpect(content().string("x".repeat(2 * 1024)));
    }

    @Test
    void loginProtectedAndRefreshFlowIsRepeatable() throws Exception {
        var login = mockMvc.perform(post("/api/v1/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("""
                                {"username":"developer","password":"probe","expiresInSeconds":120}
                                """))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.tokenType").value("Bearer"))
                .andReturn().getResponse().getContentAsString();
        String accessToken = JsonPath.read(login, "$.accessToken");
        String refreshToken = JsonPath.read(login, "$.refreshToken");

        mockMvc.perform(get("/api/v1/auth/protected")
                        .header("Authorization", "Bearer " + accessToken))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.authenticated").value(true));

        var refreshed = mockMvc.perform(post("/api/v1/auth/refresh")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"refreshToken\":\"" + refreshToken + "\"}"))
                .andExpect(status().isOk())
                .andReturn().getResponse().getContentAsString();
        String newAccessToken = JsonPath.read(refreshed, "$.accessToken");

        mockMvc.perform(get("/api/v1/auth/protected")
                        .header("Authorization", "Bearer " + accessToken))
                .andExpect(status().isUnauthorized());
        mockMvc.perform(get("/api/v1/auth/protected")
                        .header("Authorization", "Bearer " + newAccessToken))
                .andExpect(status().isOk());
    }

    @Test
    void expiredAndInvalidCredentialsAreDiagnosable() throws Exception {
        mockMvc.perform(post("/api/v1/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("""
                                {"username":"wrong","password":"wrong"}
                                """))
                .andExpect(status().isUnauthorized())
                .andExpect(jsonPath("$.detail").value("Invalid username or password"));

        var login = mockMvc.perform(post("/api/v1/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("""
                                {"username":"developer","password":"probe","expiresInSeconds":0}
                                """))
                .andExpect(status().isOk())
                .andReturn().getResponse().getContentAsString();
        String accessToken = JsonPath.read(login, "$.accessToken");

        mockMvc.perform(get("/api/v1/auth/protected")
                        .header("Authorization", "Bearer " + accessToken))
                .andExpect(status().isUnauthorized())
                .andExpect(jsonPath("$.detail").value("Access token has expired"));
    }

    private static byte[] expectedBinaryResponse() {
        var bytes = new byte[256];
        for (var index = 0; index < bytes.length; index++) {
            bytes[index] = (byte) index;
        }
        return bytes;
    }
}
