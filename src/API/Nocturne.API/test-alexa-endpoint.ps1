# Test script to verify Alexa endpoint functionality
# Run this after starting the API to test the /api/v1/alexa endpoint

$baseUrl = "https://localhost:7195"
$endpoint = "$baseUrl/api/v1/alexa"

# Test 1: Launch Request
$launchRequest = @{
    version = "1.0"
    session = @{
        new = $true
        sessionId = "amzn1.echo-api.session.test123"
        application = @{
            applicationId = "amzn1.ask.skill.test"
        }
        user = @{
            userId = "amzn1.ask.account.test"
        }
    }
    request = @{
        type = "LaunchRequest"
        requestId = "amzn1.echo-api.request.test123"
        timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
        locale = "en-US"
    }
} | ConvertTo-Json -Depth 10

Write-Host "Testing Launch Request..."
try {
    $response = Invoke-RestMethod -Uri $endpoint -Method POST -Body $launchRequest -ContentType "application/json" -SkipCertificateCheck
    Write-Host "✅ Launch Request successful"
    Write-Host "Response: $($response.response.outputSpeech.text)"
} catch {
    Write-Host "❌ Launch Request failed: $($_.Exception.Message)"
}

# Test 2: Intent Request - NSStatus
$statusRequest = @{
    version = "1.0"
    session = @{
        new = $false
        sessionId = "amzn1.echo-api.session.test123"
        application = @{
            applicationId = "amzn1.ask.skill.test"
        }
        user = @{
            userId = "amzn1.ask.account.test"
        }
    }
    request = @{
        type = "IntentRequest"
        requestId = "amzn1.echo-api.request.test456"
        timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
        locale = "en-US"
        intent = @{
            name = "NSStatus"
        }
    }
} | ConvertTo-Json -Depth 10

Write-Host "`nTesting Status Intent Request..."
try {
    $response = Invoke-RestMethod -Uri $endpoint -Method POST -Body $statusRequest -ContentType "application/json" -SkipCertificateCheck
    Write-Host "✅ Status Intent Request successful"
    Write-Host "Response: $($response.response.outputSpeech.text)"
} catch {
    Write-Host "❌ Status Intent Request failed: $($_.Exception.Message)"
}

# Test 3: Intent Request - Help
$helpRequest = @{
    version = "1.0"
    session = @{
        new = $false
        sessionId = "amzn1.echo-api.session.test123"
        application = @{
            applicationId = "amzn1.ask.skill.test"
        }
        user = @{
            userId = "amzn1.ask.account.test"
        }
    }
    request = @{
        type = "IntentRequest"
        requestId = "amzn1.echo-api.request.test789"
        timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
        locale = "en-US"
        intent = @{
            name = "AMAZON.HelpIntent"
        }
    }
} | ConvertTo-Json -Depth 10

Write-Host "`nTesting Help Intent Request..."
try {
    $response = Invoke-RestMethod -Uri $endpoint -Method POST -Body $helpRequest -ContentType "application/json" -SkipCertificateCheck
    Write-Host "✅ Help Intent Request successful"
    Write-Host "Response: $($response.response.outputSpeech.text)"
} catch {
    Write-Host "❌ Help Intent Request failed: $($_.Exception.Message)"
}

# Test 4: Session Ended Request
$sessionEndedRequest = @{
    version = "1.0"
    session = @{
        new = $false
        sessionId = "amzn1.echo-api.session.test123"
        application = @{
            applicationId = "amzn1.ask.skill.test"
        }
        user = @{
            userId = "amzn1.ask.account.test"
        }
    }
    request = @{
        type = "SessionEndedRequest"
        requestId = "amzn1.echo-api.request.test999"
        timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
        locale = "en-US"
    }
} | ConvertTo-Json -Depth 10

Write-Host "`nTesting Session Ended Request..."
try {
    $response = Invoke-RestMethod -Uri $endpoint -Method POST -Body $sessionEndedRequest -ContentType "application/json" -SkipCertificateCheck
    Write-Host "✅ Session Ended Request successful"
    if ($response.response.outputSpeech -eq $null) {
        Write-Host "Response: Empty (as expected for session end)"
    } else {
        Write-Host "Response: $($response.response.outputSpeech.text)"
    }
} catch {
    Write-Host "❌ Session Ended Request failed: $($_.Exception.Message)"
}

Write-Host "`n🎯 Alexa endpoint testing complete!"
Write-Host "All tests should pass if the API is running with proper authorization."
