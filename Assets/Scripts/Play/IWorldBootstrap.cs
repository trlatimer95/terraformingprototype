namespace Terraform.Play
{
    /// <summary>
    /// A component that builds a world of its own, and must therefore be the only one in
    /// its scene.
    ///
    /// P0Bootstrap spawns itself into any scene that has no world in it, which is what
    /// makes the plain demo work with no scene setup at all. The cost is that every other
    /// bootstrap has to be known to that check, and naming them one by one does not
    /// survive somebody adding a fourth: the span gate was listed, the hybrid was not, and
    /// the result was two worlds on the same ground -- two HUDs printing over each other,
    /// one bootstrap's keys firing in the other, and an intact surface mesh sitting over
    /// the other's tunnels so no dug hole was ever visible.
    ///
    /// Implementing this is the opt-out. Nothing has to be added to a list.
    /// </summary>
    public interface IWorldBootstrap
    {
    }
}
