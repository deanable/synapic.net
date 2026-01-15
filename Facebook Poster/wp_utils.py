"""
WordPress Utility Functions

This module contains utility functions for managing WordPress settings
that are not covered by the standard REST API.
"""

import requests
from config import Config


class WordPressSettings:
    """Utility class for managing WordPress site settings."""
    
    def __init__(self):
        self.url = Config.WORDPRESS_URL
        self.user = Config.WORDPRESS_USER
        self.password = Config.WORDPRESS_APP_PASSWORD
        
        if self.url:
            base = self.url.rstrip("/")
            self.api_url = f"{base}/wp-json/wp/v2"
            self.settings_url = f"{base}/wp-json/wp/v2/settings"
        
        self.auth = (self.user, self.password) if self.user and self.password else None
    
    def get_permalink_structure(self):
        """
        Get the current permalink structure.
        
        Returns:
            str: Current permalink structure or None if failed.
        """
        try:
            response = requests.get(
                self.settings_url,
                auth=self.auth
            )
            response.raise_for_status()
            settings = response.json()
            return settings.get('permalink_structure', 'Unknown')
        except requests.exceptions.HTTPError as e:
            if e.response.status_code == 401:
                print("Authentication failed. Ensure your user has 'Administrator' role.")
            elif e.response.status_code == 403:
                print("Access forbidden. The REST API settings endpoint may require additional permissions.")
            else:
                print(f"HTTP Error: {e}")
            return None
        except Exception as e:
            print(f"Error getting permalink structure: {e}")
            return None
    
    def set_permalink_structure(self, structure="/%postname%/"):
        """
        Set the permalink structure for the WordPress site.
        
        Common structures:
            - Plain: "" (empty string)
            - Day and name: "/%year%/%monthnum%/%day%/%postname%/"
            - Month and name: "/%year%/%monthnum%/%postname%/"
            - Numeric: "/archives/%post_id%"
            - Post name (SEO friendly): "/%postname%/"
            - Custom: Any valid structure string
        
        Args:
            structure (str): The permalink structure to set. Default is "/%postname%/" (Post name).
        
        Returns:
            bool: True if successful, False otherwise.
        """
        if not self.auth:
            print("WordPress credentials not configured.")
            return False
        
        try:
            # The WordPress REST API allows updating settings if user has proper permissions
            response = requests.post(
                self.settings_url,
                json={"permalink_structure": structure},
                auth=self.auth
            )
            response.raise_for_status()
            
            result = response.json()
            new_structure = result.get('permalink_structure', '')
            
            if new_structure == structure:
                print(f"✓ Permalink structure successfully changed to: {structure}")
                return True
            else:
                print(f"Warning: Expected '{structure}' but got '{new_structure}'")
                return False
                
        except requests.exceptions.HTTPError as e:
            if e.response.status_code == 401:
                print("Authentication failed. Check your username and application password.")
            elif e.response.status_code == 403:
                print("Access forbidden. Your user may not have permission to modify settings.")
                print("Ensure your WordPress user has the 'Administrator' role.")
            else:
                print(f"HTTP Error: {e}")
                if hasattr(e.response, 'text'):
                    print(f"Response: {e.response.text}")
            return False
        except Exception as e:
            print(f"Error setting permalink structure: {e}")
            return False
    
    def set_postname_permalinks(self):
        """
        Convenience method to set permalinks to the SEO-friendly 'Post name' structure.
        
        Returns:
            bool: True if successful, False otherwise.
        """
        print("Setting permalink structure to 'Post name' (/%postname%/) for SEO...")
        return self.set_permalink_structure("/%postname%/")


def change_permalink_to_postname():
    """
    Standalone function to change WordPress permalinks to postname structure.
    Can be run directly or imported and called from other modules.
    """
    wp = WordPressSettings()
    
    # First, show current structure
    print(f"\nWordPress Site: {wp.url}")
    print("-" * 50)
    
    current = wp.get_permalink_structure()
    if current is not None:
        if current == "":
            print(f"Current structure: Plain (no pretty permalinks)")
        else:
            print(f"Current structure: {current}")
    
    # Change to postname
    print()
    success = wp.set_postname_permalinks()
    
    if success:
        print("\n✓ Done! Your WordPress posts will now use SEO-friendly URLs like:")
        print(f"  {wp.url}/your-post-title/")
        print("\nNote: You may need to flush rewrite rules by visiting")
        print("      Settings → Permalinks in WordPress admin and clicking 'Save Changes'")
    
    return success


if __name__ == "__main__":
    change_permalink_to_postname()
