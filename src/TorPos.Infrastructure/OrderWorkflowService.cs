using TorPos.Core;
namespace TorPos.Infrastructure;

public sealed record OrderOverview(long Id,long ParkNumber,long PickupNumber,string Created,string State,string Payment,string Note,long Version,bool Open)
{
 public string StateText => State switch { "PREPARING"=>"In Vorbereitung", "READY"=>"Abholbereit", "DELIVERED"=>"Ausgegeben", _=>"Angenommen" };
 public override string ToString() => $"{Created} · P{ParkNumber:000000}"+(PickupNumber>0?$" · Abholnr. {PickupNumber:000}":"")+$" · {StateText} · {Payment}";
}
// Preparation never creates a sale. Payment comes from the checkout's persisted result.
public sealed class OrderWorkflowService(SqliteDatabase db)
{
 public Task<IReadOnlyList<OrderOverview>> ListAsync(bool training,bool includeDelivered=false) => IoQueue.RunAsync(async () => {
  var rows=new List<OrderOverview>(); await using var c=db.OpenConnection(); await using var q=c.CreateCommand();
  q.CommandText="""
   SELECT p.id,p.park_number,p.pickup_number,p.created_at,p.preparation_state,
   CASE WHEN p.status='OPEN' THEN 'Nicht bezahlt'
        WHEN p.status='SIMULATED' THEN 'TEST · ' || p.simulation_payment
        WHEN s.payment_method='CASH' THEN 'Bar bezahlt'
        WHEN s.payment_method='CARD' THEN 'Karte bezahlt'
        WHEN s.payment_method='MIXED' THEN 'Bar/Karte bezahlt'
        ELSE 'Bezahlt · siehe Bon' END,
   p.order_note,p.workflow_version,p.status
   FROM parked_receipts p LEFT JOIN sales s ON s.id=p.cashed_sale_id
   WHERE p.is_training=$training AND p.status IN ('OPEN','CASHED','SIMULATED')
   AND ($all=1 OR p.preparation_state<>'DELIVERED' OR p.status='OPEN')
   ORDER BY p.id DESC LIMIT 500;
   """;
  q.Parameters.AddWithValue("$training",training?1:0);q.Parameters.AddWithValue("$all",includeDelivered?1:0);
  await using var r=await q.ExecuteReaderAsync();while(await r.ReadAsync())rows.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),DateTimeOffset.Parse(r.GetString(3)).LocalDateTime.ToString("dd.MM HH:mm"),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt64(7),r.GetString(8)=="OPEN"));
  return (IReadOnlyList<OrderOverview>)rows;
 });
 public Task UpdateAsync(long id,long version,string state,string note,string actor,bool training) => IoQueue.RunAsync(async () => {
  if(state is not ("ACCEPTED" or "PREPARING" or "READY" or "DELIVERED"))throw new InvalidOperationException("Ungültiger Bestellstatus.");
  note=note.Trim();if(note.Length>300)throw new InvalidOperationException("Hinweis: maximal 300 Zeichen.");
  await using var c=db.OpenConnection();await using var tx=c.BeginTransaction();await using var q=c.CreateCommand();q.Transaction=tx;
  q.CommandText="""
   UPDATE parked_receipts SET preparation_state=$state,order_note=$note,workflow_version=workflow_version+1,updated_at=$at
   WHERE id=$id AND workflow_version=$version AND is_training=$training AND status IN ('OPEN','CASHED','SIMULATED')
   AND (preparation_state=$state OR (preparation_state='ACCEPTED' AND $state='PREPARING') OR (preparation_state='PREPARING' AND $state='READY') OR (preparation_state='READY' AND $state='DELIVERED'));
   """;
  q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$version",version);q.Parameters.AddWithValue("$state",state);q.Parameters.AddWithValue("$note",note);q.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));q.Parameters.AddWithValue("$training",training?1:0);
  if(await q.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("Bestellung geändert oder Statusfolge ungültig. Bitte aktualisieren.");
  q.Parameters.AddWithValue("$actor",actor);q.CommandText="INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details) VALUES($at,$actor,'ORDER_WORKFLOW','PARKED_RECEIPT',$id,$state);";await q.ExecuteNonQueryAsync();await tx.CommitAsync();
 });
 public Task CompleteSimulationAsync(long id,PaymentMethod method,bool training) => IoQueue.RunAsync(async () => {
  await using var c=db.OpenConnection();await using var q=c.CreateCommand();q.CommandText="UPDATE parked_receipts SET status='SIMULATED',simulation_payment=$method,workflow_version=workflow_version+1 WHERE id=$id AND status='OPEN' AND is_training=$training;";
  q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$method",method switch{PaymentMethod.Cash=>"Bar simuliert",PaymentMethod.Mixed=>"Bar/Karte simuliert",_=>"Karte simuliert"});q.Parameters.AddWithValue("$training",training?1:0);
  if(await q.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("Testbestellung ist nicht mehr offen.");
 });
}
