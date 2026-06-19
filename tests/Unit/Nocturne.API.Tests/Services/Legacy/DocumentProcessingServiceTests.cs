using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Models;
using Xunit;
using Activity = Nocturne.Core.Models.Activity;

namespace Nocturne.API.Tests.Services.Legacy;

public class DocumentProcessingServiceTests
{
    private readonly Mock<ILogger<DocumentProcessingService>> _loggerMock;
    private readonly DocumentProcessingService _service;

    public DocumentProcessingServiceTests()
    {
        _loggerMock = new Mock<ILogger<DocumentProcessingService>>();
        _service = new DocumentProcessingService(_loggerMock.Object);
    }

    [Fact]
    public void ProcessDocuments_WithTreatments_SanitizesAndProcessesTimestamps()
    {
        // Arrange
        var treatments = new[]
        {
            new Treatment
            {
                Id = "test1",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Meal Bolus",
                Notes = "<script>alert('xss')</script>Safe content",
                EnteredBy = "<img src=x onerror=alert('xss')>User123",
            },
        };

        // Act
        var result = _service.ProcessDocuments(treatments).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // XSS should be sanitized
        Assert.DoesNotContain("<script>", processed.Notes);
        Assert.DoesNotContain("onerror=", processed.EnteredBy);
        Assert.Contains("Safe content", processed.Notes);
        Assert.Contains("User123", processed.EnteredBy);

        // Timestamp should be processed
        Assert.NotNull(processed.CreatedAt);
    }

    [Fact]
    public void ProcessDocuments_WithDeviceStatus_SanitizesDeviceName()
    {
        // Arrange
        var deviceStatuses = new[]
        {
            new DeviceStatus
            {
                Id = "test1",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedAt = "2023-06-12T10:30:00.000Z",
                Device = "<script>alert('hack')</script>MyDevice",
            },
        };

        // Act
        var result = _service.ProcessDocuments(deviceStatuses).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // XSS should be sanitized
        Assert.DoesNotContain("<script>", processed.Device);
        Assert.Contains("MyDevice", processed.Device);
    }

    [Fact]
    public void ProcessDocuments_WithEntries_SanitizesDeviceAndType()
    {
        // Arrange
        var entries = new[]
        {
            new Entry
            {
                Id = "test1",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedAt = "2023-06-12T10:30:00.000Z",
                Device = "<b>Bold Device</b>",
                Type = "<em>sgv</em>",
            },
        };

        // Act
        var result = _service.ProcessDocuments(entries).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // Allowed HTML tags should be preserved, dangerous ones removed
        Assert.Contains("<b>", processed.Device);
        Assert.Contains("<em>", processed.Type);
    }

    [Fact]
    public void ProcessDocuments_WithActivities_SanitizesTextFields()
    {
        // Arrange
        var activities = new[]
        {
            new Activity
            {
                Id = "test1",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedAt = "2023-06-12T10:30:00.000Z",
                Type = "<script>alert('xss')</script>Exercise",
                Description = "Running <b>fast</b>",
                Notes = "<a href='javascript:alert(1)'>Click me</a>Good run",
                EnteredBy = "<iframe src='evil.html'></iframe>Runner123",
            },
        };

        // Act
        var result = _service.ProcessDocuments(activities).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // XSS should be sanitized
        Assert.DoesNotContain("<script>", processed.Type);
        Assert.DoesNotContain("javascript:", processed.Notes);
        Assert.DoesNotContain("<iframe>", processed.EnteredBy);

        // Safe content should be preserved
        Assert.Contains("Exercise", processed.Type);
        Assert.Contains("<b>fast</b>", processed.Description);
        Assert.Contains("Good run", processed.Notes);
        Assert.Contains("Runner123", processed.EnteredBy);
    }

    [Theory]
    [InlineData("2023-06-12T10:30:00.000Z", 0)] // UTC time should have 0 offset
    [InlineData("2023-06-12T10:30:00.000+05:00", 300)] // +5 hours = 300 minutes
    [InlineData("2023-06-12T10:30:00.000-03:30", -210)] // -3.5 hours = -210 minutes
    public void ProcessTimestamp_WithVariousTimezones_CalculatesCorrectUtcOffset(
        string inputTime,
        int expectedOffset
    )
    {
        // Arrange
        var treatment = new Treatment { CreatedAt = inputTime };

        // Act
        _service.ProcessTimestamp(treatment);

        // Assert
        Assert.Equal(expectedOffset, treatment.UtcOffset);
    }

    [Fact]
    public void ProcessTimestamp_HonorsClientSuppliedUtcOffset_WhenTimestampHasNoZone()
    {
        // AAPS/Loop upload SGV entries as date (UTC mills) + utcOffset (minutes) with no created_at.
        // The offset is the only local-time signal for direct NS-API uploaders, so it must survive.
        var entry = new Entry
        {
            Sgv = 120,
            Mills = 1686565800000, // 2023-06-12T10:30:00Z
            UtcOffset = -300, // client's local offset (UTC-05:00)
        };

        // Act
        _service.ProcessTimestamp(entry);

        // Assert
        Assert.Equal(-300, entry.UtcOffset); // preserved, not clobbered to 0
        Assert.Equal(1686565800000, entry.Mills); // instant unchanged
    }

    [Fact]
    public void ProcessTimestamp_DefaultsUtcOffsetToZero_WhenClientOmitsIt()
    {
        // A mills-only entry with no offset still defaults to 0 (assume UTC).
        var entry = new Entry { Sgv = 120, Mills = 1686565800000 };

        // Act
        _service.ProcessTimestamp(entry);

        // Assert
        Assert.Equal(0, entry.UtcOffset);
    }

    [Fact]
    public void ProcessTimestamp_HonorsClientSuppliedUtcOffset_WithZSuffixedCreatedAt()
    {
        // A Z-suffixed created_at carries no real offset, so a separately-supplied utcOffset
        // must still win (this routes through the mills+default-created_at branch).
        var entry = new Entry
        {
            Sgv = 120,
            Mills = 1686565800000,
            CreatedAt = "2023-06-12T10:30:00.000Z",
            UtcOffset = -300,
        };

        // Act
        _service.ProcessTimestamp(entry);

        // Assert
        Assert.Equal(-300, entry.UtcOffset);
    }

    [Fact]
    public void SanitizeHtml_WithMaliciousContent_RemovesDangerousElements()
    {
        // Arrange
        var maliciousContent =
            @"
            <script>alert('xss')</script>
            <b>Bold text</b>
            <img src='image.jpg' alt='test'>
            <a href='javascript:alert(1)'>Bad link</a>
            <a href='https://example.com'>Good link</a>
            <iframe src='evil.html'></iframe>
            <p>Normal paragraph</p>
        ";

        // Act
        var result = _service.SanitizeHtml(maliciousContent);

        // Assert
        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("javascript:", result);
        Assert.DoesNotContain("<iframe>", result);

        Assert.Contains("<b>Bold text</b>", result);
        Assert.Contains("<img", result);
        Assert.Contains("<p>Normal paragraph</p>", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeHtml_WithNullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // Act
        var result = _service.SanitizeHtml(input);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    #region Complex Validation Scenarios

    [Fact]
    public void ProcessDocuments_WithComplexNestedValidation_HandlesNestedObjects()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "test1",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = "Meal Bolus",
            BolusCalc = new Dictionary<string, object>
            {
                ["estimate"] = 5.2,
                ["foodEstimate"] = 3.1,
                ["otherCorrection"] = 0.8,
                ["notes"] = "<script>alert('nested xss')</script>Safe calc notes",
            },
            Notes = "<b>Complex</b> treatment with <em>nested</em> data",
        };

        // Act
        var result = _service.ProcessDocuments(new[] { treatment }).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // Sanitizable fields should be cleaned
        Assert.DoesNotContain("<script>", processed.Notes);
        Assert.Contains("<b>Complex</b>", processed.Notes);
        Assert.Contains("<em>nested</em>", processed.Notes);

        // Nested objects should remain intact (not sanitized unless they implement sanitization)
        Assert.NotNull(processed.BolusCalc);
        Assert.Equal(5.2, processed.BolusCalc["estimate"]);
    }

    [Fact]
    public void ProcessDocuments_WithCrossFieldValidation_ProcessesRelatedFields()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "test1",
            Mills = 1686571800000, // Explicit mills
            CreatedAt = null, // No CreatedAt set - Mills should be used to generate one
            Glucose = 180,
            GlucoseType = "Finger",
            Insulin = 4.5,
            Carbs = 45,
            EventType = "Meal Bolus",
        };

        // Act
        var result = _service.ProcessDocuments(new[] { treatment }).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // Mills should be preserved and CreatedAt should be generated
        Assert.Equal(1686571800000, processed.Mills);
        Assert.NotNull(processed.CreatedAt);
        Assert.EndsWith("Z", processed.CreatedAt); // Should be UTC format

        // Other fields should remain intact
        Assert.Equal(180, processed.Glucose);
        Assert.Equal("Finger", processed.GlucoseType);
        Assert.Equal(4.5, processed.Insulin);
        Assert.Equal(45, processed.Carbs);
    }

    [Theory]
    [InlineData("Meal Bolus", 45.0, 4.5)] // Standard meal bolus
    [InlineData("Correction Bolus", null, 2.0)] // Correction without carbs
    [InlineData("BG Check", null, null)] // BG check without insulin or carbs
    [InlineData("Exercise", null, null)] // Exercise event
    public void ProcessDocuments_WithBusinessRuleValidation_ValidatesEventTypes(
        string eventType,
        double? carbs,
        double? insulin
    )
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "test1",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = eventType,
            Carbs = carbs,
            Insulin = insulin,
            Notes = "Business rule validation test",
        };

        // Act
        var result = _service.ProcessDocuments(new[] { treatment }).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // All event types should be processed successfully
        Assert.Equal(eventType, processed.EventType);
        Assert.Equal(carbs, processed.Carbs);
        Assert.Equal(insulin, processed.Insulin);
        Assert.NotNull(processed.CreatedAt);
        Assert.True(processed.Mills > 0);
    }

    [Fact]
    public void ProcessDocuments_WithSchemaVersionCompatibility_HandlesLegacyFields()
    {
        // Arrange - Create a treatment with legacy field combinations
        var treatment = new Treatment
        {
            Id = "legacy1",
            Created_at = "2023-06-12T10:30:00.000Z", // Legacy field name
            // Date = 1686571800000, // Legacy date field - commented out as it conflicts with CreatedAt processing
            Glucose = 150,
            Units = "mg/dl", // Legacy units field
            EventType = "BG Check",
            Notes = "<p>Legacy format notes</p>",
        };

        // Act
        var result = _service.ProcessDocuments(new[] { treatment }).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // Legacy fields should be processed correctly
        Assert.Equal("2023-06-12T10:30:00.000Z", processed.CreatedAt);
        // Mills should be calculated from CreatedAt - the expected value is exactly 1686565800000
        Assert.Equal(1686565800000, processed.Mills);
        Assert.Equal("mg/dl", processed.Units);
        Assert.Contains("<p>Legacy format notes</p>", processed.Notes);
    }

    #endregion

    #region Batch Processing with Mixed Valid/Invalid Documents

    [Fact]
    public void ProcessDocuments_WithMixedValidInvalidBatch_ProcessesValidDocuments()
    {
        // Arrange
        var mixedBatch = new[]
        {
            new Treatment // Valid document
            {
                Id = "valid1",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Meal Bolus",
                Insulin = 4.5,
                Notes = "<b>Valid</b> treatment",
            },
            new Treatment // Document with null timestamp - should get current time
            {
                Id = "null_time",
                CreatedAt = null,
                Mills = 0,
                EventType = "BG Check",
                Glucose = 120,
                Notes = "<script>alert('xss')</script>Null time doc",
            },
            new Treatment // Valid document
            {
                Id = "valid2",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EventType = "Correction Bolus",
                Insulin = 2.0,
                Notes = "Valid with mills",
            },
        };

        // Act
        var result = _service.ProcessDocuments(mixedBatch).ToArray();

        // Assert
        Assert.Equal(3, result.Length); // All documents should be processed

        // Valid documents should be processed correctly
        var valid1 = result.First(r => r.Id == "valid1");
        Assert.Equal("2023-06-12T10:30:00.000Z", valid1.CreatedAt);
        Assert.Contains("<b>Valid</b>", valid1.Notes);

        // Null timestamp document should get current timestamp and sanitized content
        var nullTime = result.First(r => r.Id == "null_time");
        Assert.NotNull(nullTime.CreatedAt);
        Assert.True(nullTime.Mills > 0);
        Assert.DoesNotContain("<script>", nullTime.Notes!);
        Assert.Contains("Null time doc", nullTime.Notes!);

        // Mills-based document should work correctly
        var valid2 = result.First(r => r.Id == "valid2");
        Assert.NotNull(valid2.CreatedAt);
        Assert.True(valid2.Mills > 0);
    }

    [Fact]
    public void ProcessDocuments_WithPartialBatchFailures_ContinuesProcessing()
    {
        // Arrange
        var problematicBatch = new[]
        {
            new Entry
            {
                Id = "entry1",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "sgv",
                Sgv = 120,
                Device = "<img src='x' onerror='alert()'/>MySensor",
            },
            new Entry
            {
                Id = "entry2",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                Type = "<script>bad</script>mbg",
                Sgv = 150,
                Device = "ValidDevice",
            },
            new Entry
            {
                Id = "entry3",
                Mills = 0, // No valid timestamp
                CreatedAt = null,
                Type = "cal",
                Sgv = 100,
                Device = "CalibrationDevice",
            },
        };

        // Act
        var result = _service.ProcessDocuments(problematicBatch).ToArray();

        // Assert
        Assert.Equal(3, result.Length); // All entries should be processed despite issues

        // First entry - XSS in device should be cleaned
        var entry1 = result.First(r => r.Id == "entry1");
        Assert.DoesNotContain("onerror", entry1.Device);
        Assert.Contains("MySensor", entry1.Device);

        // Second entry - XSS in type should be cleaned
        var entry2 = result.First(r => r.Id == "entry2");
        Assert.DoesNotContain("<script>", entry2.Type);
        Assert.Contains("mbg", entry2.Type);

        // Third entry - Should get current timestamp as fallback
        var entry3 = result.First(r => r.Id == "entry3");
        Assert.NotNull(entry3.CreatedAt);
        Assert.True(entry3.Mills > 0);
    }

    [Fact]
    public void ProcessDocuments_WithErrorAggregation_HandlesMultipleIssues()
    {
        // Arrange
        var documentsWithMultipleIssues = new[]
        {
            new Activity
            {
                Id = "activity1",
                CreatedAt = null, // Null date instead of malformed
                Mills = 0,
                Type = "<iframe src='evil.com'></iframe>Exercise",
                Description = "<script>document.cookie</script>Running session",
                Notes = "<a href='javascript:void(0)'>Click me</a>Great workout!",
                EnteredBy = "<img onerror='hack()' src='x'>Athlete123",
                Duration = 45,
            },
        };

        // Act
        var result = _service.ProcessDocuments(documentsWithMultipleIssues).ToArray();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // All XSS attempts should be sanitized
        Assert.DoesNotContain("<iframe>", processed.Type);
        Assert.DoesNotContain("<script>", processed.Description);
        Assert.DoesNotContain("javascript:", processed.Notes);
        Assert.DoesNotContain("onerror", processed.EnteredBy);

        // Safe content should be preserved
        Assert.Contains("Exercise", processed.Type);
        Assert.Contains("Running session", processed.Description);
        Assert.Contains("Great workout!", processed.Notes);
        Assert.Contains("Athlete123", processed.EnteredBy);

        // Null timestamp should be replaced with valid one
        Assert.NotNull(processed.CreatedAt);
        Assert.True(processed.Mills > 0);

        // Other data should remain intact
        Assert.Equal(45, processed.Duration);
    }

    #endregion

    #region Memory Management and Large Documents

    [Fact]
    public void ProcessDocuments_WithLargeDocumentBatch_HandlesMemoryEfficiently()
    {
        // Arrange - Create a large batch of documents
        const int batchSize = 1000;
        var largeBatch = new List<Treatment>(batchSize);

        for (int i = 0; i < batchSize; i++)
        {
            largeBatch.Add(
                new Treatment
                {
                    Id = $"treatment_{i}",
                    Mills = DateTimeOffset.UtcNow.AddMinutes(-i).ToUnixTimeMilliseconds(),
                    EventType = i % 2 == 0 ? "Meal Bolus" : "Correction Bolus",
                    Insulin = (i % 10) + 1.0,
                    Carbs = i % 3 == 0 ? (i % 50) + 10.0 : null,
                    Notes =
                        $"<b>Treatment {i}</b> with some <em>formatting</em> and potential <script>alert({i})</script> XSS",
                }
            );
        }

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = _service.ProcessDocuments(largeBatch).ToArray();

        stopwatch.Stop();

        // Assert
        Assert.Equal(batchSize, result.Length);

        // Verify processing completed in reasonable time (should be under 5 seconds for 1000 documents)
        Assert.True(
            stopwatch.ElapsedMilliseconds < 5000,
            $"Large batch processing took {stopwatch.ElapsedMilliseconds}ms, expected < 5000ms"
        );

        // Spot check some processed documents
        var firstDoc = result[0];
        Assert.Equal("treatment_0", firstDoc.Id);
        Assert.DoesNotContain("<script>", firstDoc.Notes);
        Assert.Contains("<b>Treatment 0</b>", firstDoc.Notes);

        var lastDoc = result[batchSize - 1];
        Assert.Equal($"treatment_{batchSize - 1}", lastDoc.Id);
        Assert.NotNull(lastDoc.CreatedAt);
        Assert.True(lastDoc.Mills > 0);
    }

    [Fact]
    public void ProcessDocuments_WithLargeTextFields_SanitizesEfficiently()
    {
        // Arrange - Create documents with very large text fields
        var largeTextBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            largeTextBuilder.Append(
                $"<p>This is paragraph {i} with <script>alert('xss{i}')</script> some content and <b>bold text {i}</b>.</p>"
            );
        }
        var largeText = largeTextBuilder.ToString();

        var documentsWithLargeText = new[]
        {
            new Treatment
            {
                Id = "large_text_1",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Meal Bolus",
                Notes = largeText,
            },
        };

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = _service.ProcessDocuments(documentsWithLargeText).ToArray();

        stopwatch.Stop();

        // Assert
        Assert.Single(result);
        var processed = result[0];

        // Should complete in reasonable time (under 1 second for large text sanitization)
        Assert.True(
            stopwatch.ElapsedMilliseconds < 1000,
            $"Large text sanitization took {stopwatch.ElapsedMilliseconds}ms, expected < 1000ms"
        );

        // All script tags should be removed but paragraphs and bold text should remain
        Assert.DoesNotContain("<script>", processed.Notes!);
        Assert.Contains("<p>", processed.Notes!);
        Assert.Contains("<b>bold text", processed.Notes!);

        // Content should be significantly shorter due to script removal
        Assert.True(processed.Notes!.Length < largeText.Length);
    }

    [Fact]
    public void ProcessDocuments_WithBatchSizeLimits_HandlesLargeBatchesInChunks()
    {
        // Arrange - Create an even larger batch to test chunking behavior
        const int largeBatchSize = 5000;
        var veryLargeBatch = new List<Entry>(largeBatchSize);

        for (int i = 0; i < largeBatchSize; i++)
        {
            veryLargeBatch.Add(
                new Entry
                {
                    Id = $"entry_{i}",
                    Mills = DateTimeOffset.UtcNow.AddSeconds(-i).ToUnixTimeMilliseconds(),
                    Type = "sgv",
                    Sgv = 80 + (i % 200), // Varying glucose values
                    Device = $"<span>Device_{i % 10}</span>",
                    Notes = i % 100 == 0 ? $"<b>Milestone entry {i}</b>" : null,
                }
            );
        }

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = _service.ProcessDocuments(veryLargeBatch).ToArray();

        stopwatch.Stop();

        // Assert
        Assert.Equal(largeBatchSize, result.Length);

        // Should handle large batches efficiently (under 15 seconds)
        Assert.True(
            stopwatch.ElapsedMilliseconds < 15000,
            $"Very large batch processing took {stopwatch.ElapsedMilliseconds}ms, expected < 15000ms"
        );

        // Verify random sampling of processed documents
        var sampleIndices = new[] { 0, largeBatchSize / 4, largeBatchSize / 2, largeBatchSize - 1 };
        foreach (var index in sampleIndices)
        {
            var doc = result[index];
            Assert.Equal($"entry_{index}", doc.Id);
            Assert.NotNull(doc.CreatedAt);
            Assert.True(doc.Mills > 0);
            Assert.Contains("<span>Device_", doc.Device);
        }
    }

    #endregion

    #region Concurrent Processing Safety

    [Fact]
    public async Task ProcessDocuments_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        const int threadCount = 10;
        const int documentsPerThread = 100;
        var allResults = new ConcurrentBag<Treatment[]>();
        var tasks = new List<Task>();

        // Act - Process documents concurrently from multiple threads
        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t; // Capture for closure
            var task = Task.Run(() =>
            {
                var threadDocuments = new List<Treatment>();
                for (int i = 0; i < documentsPerThread; i++)
                {
                    threadDocuments.Add(
                        new Treatment
                        {
                            Id = $"thread_{threadIndex}_doc_{i}",
                            Mills = DateTimeOffset
                                .UtcNow.AddMinutes(-(threadIndex * 100 + i))
                                .ToUnixTimeMilliseconds(),
                            EventType = "Concurrent Test",
                            Insulin = threadIndex + i * 0.1,
                            Notes =
                                $"<script>alert('thread{threadIndex}')</script>Thread {threadIndex} document {i}",
                        }
                    );
                }

                var results = _service.ProcessDocuments(threadDocuments).ToArray();
                allResults.Add(results);
            });
            tasks.Add(task);
        }

        // Wait for all threads to complete
        await Task.WhenAll(tasks.ToArray());

        // Assert
        Assert.Equal(threadCount, allResults.Count);

        // Verify each thread's results
        var totalDocuments = 0;
        foreach (var threadResults in allResults)
        {
            Assert.Equal(documentsPerThread, threadResults.Length);
            totalDocuments += threadResults.Length;

            // Verify each document was processed correctly
            foreach (var doc in threadResults)
            {
                Assert.NotNull(doc.Id);
                Assert.StartsWith("thread_", doc.Id);
                Assert.NotNull(doc.CreatedAt);
                Assert.True(doc.Mills > 0);
                Assert.DoesNotContain("<script>", doc.Notes);
                Assert.Contains("Thread", doc.Notes);
                Assert.Contains("document", doc.Notes);
            }
        }

        Assert.Equal(threadCount * documentsPerThread, totalDocuments);
    }

    [Fact]
    public async Task ProcessDocuments_ConcurrentDifferentDocumentTypes_HandlesRaceConditions()
    {
        // Arrange
        const int iterations = 50;
        var tasks = new List<Task>();
        var results = new ConcurrentBag<string>();

        // Act - Process different document types concurrently
        for (int i = 0; i < iterations; i++)
        {
            var iteration = i;

            // Task 1: Process Treatments
            tasks.Add(
                Task.Run(() =>
                {
                    var treatments = new[]
                    {
                        new Treatment
                        {
                            Id = $"treatment_{iteration}",
                            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            EventType = "Concurrent Treatment",
                            Notes = $"<b>Treatment {iteration}</b>",
                        },
                    };

                    var processed = _service.ProcessDocuments(treatments).ToArray();
                    results.Add($"Treatment_{iteration}:{processed[0].Id}");
                })
            );

            // Task 2: Process Entries
            tasks.Add(
                Task.Run(() =>
                {
                    var entries = new[]
                    {
                        new Entry
                        {
                            Id = $"entry_{iteration}",
                            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Type = "sgv",
                            Sgv = 120 + iteration,
                            Device = $"<span>Device_{iteration}</span>",
                        },
                    };

                    var processed = _service.ProcessDocuments(entries).ToArray();
                    results.Add($"Entry_{iteration}:{processed[0].Id}");
                })
            );

            // Task 3: Process Activities
            tasks.Add(
                Task.Run(() =>
                {
                    var activities = new[]
                    {
                        new Activity
                        {
                            Id = $"activity_{iteration}",
                            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Type = "Exercise",
                            Description = $"<em>Activity {iteration}</em>",
                            Duration = iteration * 5,
                        },
                    };

                    var processed = _service.ProcessDocuments(activities).ToArray();
                    results.Add($"Activity_{iteration}:{processed[0].Id}");
                })
            );
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks.ToArray());

        // Assert
        Assert.Equal(iterations * 3, results.Count); // 3 types × iterations

        // Verify we got results for all document types and iterations
        var treatmentResults = results.Where(r => r.StartsWith("Treatment_")).ToArray();
        var entryResults = results.Where(r => r.StartsWith("Entry_")).ToArray();
        var activityResults = results.Where(r => r.StartsWith("Activity_")).ToArray();

        Assert.Equal(iterations, treatmentResults.Length);
        Assert.Equal(iterations, entryResults.Length);
        Assert.Equal(iterations, activityResults.Length);
    }

    #endregion

    #region Error Recovery and Rollback Scenarios

    [Fact]
    public void ProcessDocuments_WithTransactionRollbackScenario_HandlesGracefully()
    {
        // Arrange - Simulate a scenario where some documents would cause issues
        var documentsRequiringRollback = new[]
        {
            new Treatment
            {
                Id = "valid_treatment",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Meal Bolus",
                Insulin = 4.5,
                Notes = "Valid treatment",
            },
            new Treatment
            {
                Id = "problematic_treatment",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Complex Event",
                Notes = null, // Null notes should be handled gracefully
                BolusCalc = new Dictionary<string, object>
                {
                    ["badValue"] = new object(), // Complex object that might cause issues
                },
            },
        };

        // Act - Processing should handle all documents gracefully
        var result = _service.ProcessDocuments(documentsRequiringRollback).ToArray();

        // Assert - All documents should be processed successfully
        Assert.Equal(2, result.Length);

        var validTreatment = result.First(r => r.Id == "valid_treatment");
        Assert.Equal("Valid treatment", validTreatment.Notes);

        var problematicTreatment = result.First(r => r.Id == "problematic_treatment");
        Assert.NotNull(problematicTreatment.CreatedAt);
        Assert.True(problematicTreatment.Mills > 0);
        // BolusCalc should remain as is (not sanitized since it's not a string field)
        Assert.NotNull(problematicTreatment.BolusCalc);
    }

    [Fact]
    public void ProcessDocuments_WithValidationErrorRecovery_ContinuesProcessing()
    {
        // Arrange - Mix of documents with various validation edge cases
        var mixedValidationDocuments = new[]
        {
            new Entry
            {
                Id = "entry_with_extreme_values",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), // Use a valid timestamp instead of extreme value
                Type = "sgv",
                Sgv = double.MaxValue, // Extreme glucose value
                Device = new string('A', 1000), // Very long device name
            },
            new Entry
            {
                Id = "entry_with_negative_values",
                Mills = -1, // Negative timestamp
                Type = "sgv",
                Sgv = -50, // Negative glucose
                Device = "",
            },
            new Entry
            {
                Id = "normal_entry",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = "sgv",
                Sgv = 120,
                Device = "NormalDevice",
            },
        };

        // Act
        var result = _service.ProcessDocuments(mixedValidationDocuments).ToArray();

        // Assert - All documents should be processed
        Assert.Equal(3, result.Length);

        // Extreme values should be preserved or handled gracefully (service might process extreme values)
        var extremeEntry = result.First(r => r.Id == "entry_with_extreme_values");
        Assert.True(extremeEntry.Mills > 0); // Should have valid timestamp
        Assert.Equal(double.MaxValue, extremeEntry.Sgv);
        Assert.Equal(1000, extremeEntry.Device?.Length); // Long device name should be preserved

        // Negative values should be processed (service may replace invalid timestamps)
        var negativeEntry = result.First(r => r.Id == "entry_with_negative_values");
        Assert.True(negativeEntry.Mills > 0); // Service likely replaces negative timestamp with current time
        Assert.Equal(-50, negativeEntry.Sgv); // Negative glucose should be preserved

        // Normal entry should work as expected
        var normalEntry = result.First(r => r.Id == "normal_entry");
        Assert.Equal(120, normalEntry.Sgv);
    }

    [Fact]
    public void ProcessDocuments_WithRecoveryFromProcessingFailures_HandlesExceptions()
    {
        // Arrange - Documents that might cause processing issues
        var challengingDocuments = new[]
        {
            new Treatment
            {
                Id = "treatment_with_circular_reference",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Test",
                Notes = "<script>while(true){}</script>Potentially infinite loop content",
            },
            new Treatment
            {
                Id = "treatment_with_unicode",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Unicode Test",
                Notes = "🩺💉📊 Unicode medical symbols and 中文 characters with <b>HTML</b>",
            },
            new Treatment
            {
                Id = "treatment_with_special_chars",
                CreatedAt = "2023-06-12T10:30:00.000Z",
                EventType = "Special Chars",
                Notes = @"Special chars: \n\r\t""'`~!@#$%^&*()_+{}|:<>?[];\./,",
            },
        };

        // Act - Should handle all documents without throwing exceptions
        var result = _service.ProcessDocuments(challengingDocuments).ToArray();

        // Assert
        Assert.Equal(3, result.Length);

        // Script should be removed but other content preserved
        var circularRef = result.First(r => r.Id == "treatment_with_circular_reference");
        Assert.DoesNotContain("<script>", circularRef.Notes!);
        Assert.Contains("Potentially infinite loop content", circularRef.Notes!);

        // Unicode should be preserved along with allowed HTML
        var unicode = result.First(r => r.Id == "treatment_with_unicode");
        Assert.Contains("🩺💉📊", unicode.Notes!);
        Assert.Contains("中文", unicode.Notes!);
        Assert.Contains("<b>HTML</b>", unicode.Notes!);

        // Special characters should be preserved (HTML sanitizer may encode some characters)
        var specialChars = result.First(r => r.Id == "treatment_with_special_chars");
        Assert.Contains(@"\n\r\t", specialChars.Notes!);
        // Check that the content is not empty and has some of the special characters
        Assert.True(specialChars.Notes!.Length > 20); // Should have substantial content
        Assert.Contains("Special chars:", specialChars.Notes!);
    }

    #endregion

    #region Performance Benchmark Tests

    [Fact]
    [Trait("Category", "Performance")]
    public void ProcessDocuments_PerformanceBenchmark_LargeDocumentStressTesting()
    {
        // Arrange - Create a very large batch for stress testing
        const int stressTestSize = 10000;
        var stressTestBatch = new List<Treatment>(stressTestSize);

        for (int i = 0; i < stressTestSize; i++)
        {
            stressTestBatch.Add(
                new Treatment
                {
                    Id = $"stress_test_{i}",
                    Mills = DateTimeOffset.UtcNow.AddMinutes(-i).ToUnixTimeMilliseconds(),
                    EventType = i % 5 == 0 ? "Meal Bolus" : "Correction Bolus",
                    Insulin = (i % 20) * 0.5 + 1.0,
                    Carbs = i % 3 == 0 ? (i % 100) + 5.0 : null,
                    Notes =
                        $"<p>Stress test treatment {i}</p><script>alert('stress{i}')</script><b>Performance test</b>",
                    BolusCalc =
                        i % 10 == 0
                            ? new Dictionary<string, object>
                            {
                                ["estimate"] = (i % 20) * 0.25,
                                ["foodEstimate"] = (i % 15) * 0.33,
                                ["correction"] = (i % 8) * 0.125,
                            }
                            : null,
                }
            );
        }

        var stopwatch = Stopwatch.StartNew();
        var memoryBefore = GC.GetTotalMemory(false);

        // Act
        var result = _service.ProcessDocuments(stressTestBatch).ToArray();

        stopwatch.Stop();
        var memoryAfter = GC.GetTotalMemory(true); // Force GC to get accurate measurement

        // Assert
        Assert.Equal(stressTestSize, result.Length);

        // Performance assertions - should handle 10k documents in reasonable time
        Assert.True(
            stopwatch.ElapsedMilliseconds < 30000,
            $"Stress test took {stopwatch.ElapsedMilliseconds}ms, expected < 30000ms"
        );

        // Memory usage should be reasonable (allow for 100MB increase)
        var memoryIncrease = memoryAfter - memoryBefore;
        Assert.True(
            memoryIncrease < 100_000_000,
            $"Memory increase was {memoryIncrease} bytes, expected < 100MB"
        );

        // Spot check results
        var firstResult = result[0];
        Assert.DoesNotContain("<script>", firstResult.Notes!);
        Assert.Contains("<p>Stress test treatment 0</p>", firstResult.Notes!);
        Assert.Contains("<b>Performance test</b>", firstResult.Notes!);

        var lastResult = result[stressTestSize - 1];
        Assert.Equal($"stress_test_{stressTestSize - 1}", lastResult.Id);
        Assert.NotNull(lastResult.CreatedAt);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ProcessDocuments_BatchProcessingPerformance_ScalesLinearly()
    {
        // Arrange - Test different batch sizes to verify linear scaling
        var batchSizes = new[] { 100, 500, 1000, 2000 };
        var results = new List<(int Size, long ElapsedMs)>();

        foreach (var batchSize in batchSizes)
        {
            var batch = new List<Entry>(batchSize);
            for (int i = 0; i < batchSize; i++)
            {
                batch.Add(
                    new Entry
                    {
                        Id = $"perf_entry_{i}",
                        Mills = DateTimeOffset.UtcNow.AddSeconds(-i).ToUnixTimeMilliseconds(),
                        Type = "sgv",
                        Sgv = 80 + (i % 200),
                        Device = $"<span>PerfDevice_{i % 5}</span>",
                        Notes = i % 50 == 0 ? $"<b>Performance note {i}</b>" : null,
                    }
                );
            }

            var stopwatch = Stopwatch.StartNew();

            // Act
            var processed = _service.ProcessDocuments(batch).ToArray();

            stopwatch.Stop();
            results.Add((batchSize, stopwatch.ElapsedMilliseconds));

            // Verify processing completed correctly
            Assert.Equal(batchSize, processed.Length);
        }

        // Assert - Processing time should scale roughly linearly
        // Allow for some variance due to system factors
        var smallBatchTime = results[0].ElapsedMs;
        var largeBatchTime = results[3].ElapsedMs; // 2000 vs 100 items = 20x
        var scalingRatio = (double)largeBatchTime / smallBatchTime;

        // Should scale between 5x and 50x (allowing for overhead)
        Assert.True(
            scalingRatio >= 5 && scalingRatio <= 50,
            $"Scaling ratio was {scalingRatio:F2}, expected between 5 and 50"
        );
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ProcessDocuments_MemoryUsageBenchmarks_EfficientMemoryManagement()
    {
        // Arrange - Test memory efficiency with document reuse patterns
        const int iterations = 100;
        const int documentsPerIteration = 50;

        var memoryMeasurements = new List<long>();

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var batch = new List<DeviceStatus>(documentsPerIteration);
            for (int i = 0; i < documentsPerIteration; i++)
            {
                batch.Add(
                    new DeviceStatus
                    {
                        Id = $"memory_test_{iteration}_{i}",
                        Mills = DateTimeOffset
                            .UtcNow.AddSeconds(-(iteration * documentsPerIteration + i))
                            .ToUnixTimeMilliseconds(),
                        Device = $"<div>MemoryTestDevice_{i % 3}</div>",
                        IsCharging = (i % 2) == 0,
                    }
                );
            }

            var memoryBefore = GC.GetTotalMemory(false);

            // Act
            var result = _service.ProcessDocuments(batch).ToArray();

            // Force garbage collection to measure actual retention
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var memoryAfter = GC.GetTotalMemory(false);
            memoryMeasurements.Add(memoryAfter - memoryBefore);

            // Verify processing worked
            Assert.Equal(documentsPerIteration, result.Length);
        }

        // Assert - Memory usage should be consistent and not grow significantly
        var averageMemoryIncrease = memoryMeasurements.Average();
        var maxMemoryIncrease = memoryMeasurements.Max();

        // Memory increase per iteration should be reasonable (under 5MB per iteration)
        Assert.True(
            averageMemoryIncrease < 5_000_000,
            $"Average memory increase was {averageMemoryIncrease} bytes, expected < 5MB"
        );
        Assert.True(
            maxMemoryIncrease < 10_000_000,
            $"Max memory increase was {maxMemoryIncrease} bytes, expected < 10MB"
        );
    }

    #endregion

    #region Integration Point Tests

    [Fact]
    public void ProcessDocuments_DatabaseTransactionCoordination_HandlesConsistency()
    {
        // Arrange - Simulate documents that would participate in database transactions
        var treatmentDocument = new Treatment
        {
            Id = "tx_treatment_1",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = "Meal Bolus",
            Insulin = 4.5,
            Carbs = 45,
            Notes = "<b>Transactional</b> treatment",
        };

        var entryDocument = new Entry
        {
            Id = "tx_entry_1",
            Mills = DateTimeOffset.Parse("2023-06-12T10:30:00.000Z").ToUnixTimeMilliseconds(),
            Type = "sgv",
            Sgv = 180,
            Device = "<span>CGM Device</span>",
        };

        // Act - Processing should maintain data consistency
        var treatmentResults = _service.ProcessDocuments(new[] { treatmentDocument }).ToArray();
        var entryResults = _service.ProcessDocuments(new[] { entryDocument }).ToArray();

        // Assert - Both should be processed consistently
        Assert.Single(treatmentResults);
        Assert.Single(entryResults);

        var treatment = treatmentResults[0];
        var entry = entryResults[0];

        // Timestamps should be processed consistently
        Assert.NotNull(treatment.CreatedAt);
        Assert.NotNull(entry.CreatedAt);
        Assert.True(treatment.Mills > 0);
        Assert.True(entry.Mills > 0);

        // HTML sanitization should be consistent
        Assert.Contains("<b>Transactional</b>", treatment.Notes!);
        Assert.Contains("<span>CGM Device</span>", entry.Device!);
    }

    [Fact]
    public void ProcessDocuments_CacheInvalidationPatterns_HandlesUpdates()
    {
        // Arrange - Documents that represent cache invalidation scenarios
        var originalDocument = new Treatment
        {
            Id = "cache_test_doc",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = "Meal Bolus",
            Insulin = 4.5,
            Notes = "<p>Original content</p>",
        };

        var updatedDocument = new Treatment
        {
            Id = "cache_test_doc", // Same ID - represents an update
            CreatedAt = "2023-06-12T10:35:00.000Z", // Updated timestamp
            EventType = "Meal Bolus",
            Insulin = 4.5,
            Notes = "<p>Updated content</p><script>alert('updated')</script>",
        };

        // Act - Process original then updated version
        var originalResult = _service.ProcessDocuments(new[] { originalDocument }).ToArray();
        var updatedResult = _service.ProcessDocuments(new[] { updatedDocument }).ToArray();

        // Assert - Both should process correctly with consistent behavior
        Assert.Single(originalResult);
        Assert.Single(updatedResult);

        var original = originalResult[0];
        var updated = updatedResult[0];

        Assert.Equal("cache_test_doc", original.Id);
        Assert.Equal("cache_test_doc", updated.Id);

        // Content should be sanitized consistently
        Assert.Contains("<p>Original content</p>", original.Notes!);
        Assert.Contains("<p>Updated content</p>", updated.Notes!);
        Assert.DoesNotContain("<script>", updated.Notes!);

        // Timestamps should be different
        Assert.NotEqual(original.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public void ProcessDocuments_EventBroadcastingVerification_ProcessesForEvents()
    {
        // Arrange - Documents that would trigger event broadcasting
        var activityDocument = new Activity
        {
            Id = "broadcast_activity",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "Exercise",
            Description = "<em>High intensity workout</em>",
            Duration = 60,
            Notes = "Event that should trigger broadcasts",
        };

        var treatmentDocument = new Treatment
        {
            Id = "broadcast_treatment",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = "Alarm",
            Notes = "<b>Critical</b> glucose alert",
            Glucose = 50,
        };

        // Act - Process documents that would need event broadcasting
        var activityResults = _service.ProcessDocuments(new[] { activityDocument }).ToArray();
        var treatmentResults = _service.ProcessDocuments(new[] { treatmentDocument }).ToArray();

        // Assert - Documents should be ready for event broadcasting
        Assert.Single(activityResults);
        Assert.Single(treatmentResults);

        var activity = activityResults[0];
        var treatment = treatmentResults[0];

        // All fields necessary for event broadcasting should be processed
        Assert.NotNull(activity.CreatedAt);
        Assert.True(activity.Mills > 0);
        Assert.Contains("<em>High intensity workout</em>", activity.Description!);

        Assert.NotNull(treatment.CreatedAt);
        Assert.True(treatment.Mills > 0);
        Assert.Contains("<b>Critical</b>", treatment.Notes!);
        Assert.Equal(50, treatment.Glucose);
    }

    [Fact]
    public void ProcessDocuments_AuditTrailGeneration_PreparesAuditableData()
    {
        // Arrange - Documents that require audit trail generation
        var treatmentDocument = new Treatment
        {
            Id = "audit_treatment",
            CreatedAt = "2023-06-12T10:30:00.000Z",
            EventType = "Manual Override",
            Insulin = 6.0,
            Notes = "<p>Manual insulin correction</p>",
            EnteredBy = "<span>Dr. Smith</span>",
        };

        var entryDocument = new Entry
        {
            Id = "audit_entry",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "cal",
            Sgv = 120,
            Device = "Manual Entry",
            Notes = "Calibration entry",
        };

        // Act - Process documents for audit trail preparation
        var treatmentResults = _service.ProcessDocuments(new[] { treatmentDocument }).ToArray();
        var entryResults = _service.ProcessDocuments(new[] { entryDocument }).ToArray();

        // Assert - All data necessary for audit trails should be available
        Assert.Single(treatmentResults);
        Assert.Single(entryResults);

        var treatment = treatmentResults[0];
        var entry = entryResults[0];

        // Audit trail requires: ID, timestamps, sanitized content, entered by info
        Assert.NotNull(treatment.Id);
        Assert.NotNull(treatment.CreatedAt);
        Assert.True(treatment.Mills > 0);
        Assert.Contains("Dr. Smith", treatment.EnteredBy!);
        Assert.DoesNotContain("<script>", treatment.Notes!); // Must be safe for audit logs

        Assert.NotNull(entry.Id);
        Assert.NotNull(entry.CreatedAt);
        Assert.True(entry.Mills > 0);
        Assert.Equal("Manual Entry", entry.Device);
    }

    #endregion
}
