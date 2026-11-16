using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace MatchmakingService.Domain;

public enum TicketStatus { Waiting, Matched, Cancelled, Expired }

public sealed record TicketView(Guid Id, string Username, TicketStatus Status, string? ServerHost, int? ServerPort)
{
    public TicketResponse ToResponse() => new(Id, Status.ToString(), ServerHost, ServerPort);
}

/// <summary>
/// FIFO-kø af spillere. Klienten får et ticket (202 Accepted) og poller status
/// ("send, vent, process") i stedet for at holde en request åben, mens der ventes på modspillere.
/// </summary>
public sealed class MatchmakingQueue(
    ServerRegistry registry,
    IOptions<MatchmakingOptions> options,
    TimeProvider time,
    ILogger<MatchmakingQueue> logger)
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Ticket> _tickets = new();
    private readonly LinkedList<Guid> _waiting = new();

    public TicketView Enqueue(string username)
    {
        lock (_lock)
        {
            // Idempotent: en spiller der allerede venter får sit eksisterende ticket
            var existing = _tickets.Values.FirstOrDefault(t =>
                t.Status == TicketStatus.Waiting &&
                string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing.ToView();

            var ticket = new Ticket(Guid.NewGuid(), username, time.GetUtcNow().UtcDateTime);
            _tickets[ticket.Id] = ticket;
            _waiting.AddLast(ticket.Id);

            FormMatchesLocked();
            return ticket.ToView();
        }
    }

    public TicketView? Get(Guid id)
    {
        lock (_lock) return _tickets.GetValueOrDefault(id)?.ToView();
    }

    public bool Cancel(Guid id, string username)
    {
        lock (_lock)
        {
            if (!_tickets.TryGetValue(id, out var ticket) ||
                ticket.Status != TicketStatus.Waiting ||
                !string.Equals(ticket.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ticket.Status = TicketStatus.Cancelled;
            _waiting.Remove(id);
            return true;
        }
    }

    public int TryFormMatches()
    {
        lock (_lock) return FormMatchesLocked();
    }

    public void RemoveExpired()
    {
        lock (_lock)
        {
            var cutoff = time.GetUtcNow().UtcDateTime.AddMinutes(-options.Value.TicketMaxAgeMinutes);
            foreach (var ticket in _tickets.Values.Where(t => t.CreatedUtc < cutoff).ToList())
            {
                if (ticket.Status == TicketStatus.Waiting)
                {
                    ticket.Status = TicketStatus.Expired;
                    _waiting.Remove(ticket.Id);
                }
                else
                {
                    _tickets.Remove(ticket.Id);
                }
            }
        }
    }

    private int FormMatchesLocked()
    {
        var size = options.Value.MatchSize;
        var formed = 0;

        while (_waiting.Count >= size)
        {
            var server = registry.TryReserve(size);
            if (server is null) break; // ingen sund server med plads – spillerne bliver i køen

            var players = new List<string>();
            for (var i = 0; i < size; i++)
            {
                var ticket = _tickets[_waiting.First!.Value];
                _waiting.RemoveFirst();
                ticket.Status = TicketStatus.Matched;
                ticket.ServerHost = server.Host;
                ticket.ServerPort = server.Port;
                players.Add(ticket.Username);
            }

            formed++;
            logger.LogInformation("Match dannet på {ServerId}: {Players}", server.ServerId, string.Join(", ", players));
        }

        return formed;
    }

    private sealed class Ticket(Guid id, string username, DateTime createdUtc)
    {
        public Guid Id { get; } = id;
        public string Username { get; } = username;
        public DateTime CreatedUtc { get; } = createdUtc;
        public TicketStatus Status { get; set; } = TicketStatus.Waiting;
        public string? ServerHost { get; set; }
        public int? ServerPort { get; set; }

        public TicketView ToView() => new(Id, Username, Status, ServerHost, ServerPort);
    }
}
